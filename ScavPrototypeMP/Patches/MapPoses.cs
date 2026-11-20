using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Together;

// the generated spot. a later move does not change it, so a reload can find the same prop.
sealed class MapPoseAnchor : MonoBehaviour {
    public bool set;
    public Vector2 birth;
    public float birthRot;
    public Vector2 birthScale;
    public bool keepRot;
    public float rot;

    public void Capture() {
        if (set)
            return;
        birth = transform.position;
        birthRot = transform.eulerAngles.z;
        birthScale = transform.localScale;
        set = true;
    }

    void LateUpdate() {
        if (!keepRot)
            return;
        if (Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.z, rot)) > 0.25f)
            transform.rotation = Quaternion.Euler(0f, 0f, rot);
        Rigidbody2D rb = GetComponent<Rigidbody2D>();
        if (rb != null)
            rb.rotation = rot;
    }
}

// debug-map poses. tiles do not remember where a prop was moved, turned, or resized.
static class MapPoses {
    class PropPose {
        public string id;
        public float bx, by, x, y, rz, sx, sy;
    }

    class PlayerPose {
        public string id;
        public float x, y, rz, sx, sy;
    }

    class FileDto {
        public List<PropPose> props = new List<PropPose>();
        public List<PlayerPose> players = new List<PlayerPose>();
    }

    const string FileName = "world_poses.json";
    static readonly List<PropPose> props = new List<PropPose>();
    static readonly Dictionary<string, PlayerPose> players = new Dictionary<string, PlayerPose>();

    internal static void Clear() {
        props.Clear();
        players.Clear();
    }

    internal static void Capture(BuildingEntity building) {
        if (building == null)
            return;
        MapPoseAnchor anchor = building.GetComponent<MapPoseAnchor>();
        if (anchor == null)
            anchor = building.gameObject.AddComponent<MapPoseAnchor>();
        anchor.Capture();
    }

    internal static void Write(string folder) {
        if (WorldGeneration.world == null)
            return;
        FileDto dto = new FileDto();
        BuildingEntity[] buildings = Object.FindObjectsOfType<BuildingEntity>(true);
        for (int i = 0; i < buildings.Length; i++) {
            BuildingEntity building = buildings[i];
            if (building == null || string.IsNullOrEmpty(building.id))
                continue;
            Transform parent = building.transform.parent;
            if (parent != null && parent.GetComponentInParent<BuildingEntity>() != null)
                continue;
            MapPoseAnchor anchor = building.GetComponent<MapPoseAnchor>();
            if (anchor == null) {
                anchor = building.gameObject.AddComponent<MapPoseAnchor>();
                anchor.Capture();
            }
            if (!Moved(building.transform, anchor))
                continue;
            Vector3 scale = building.transform.localScale;
            dto.props.Add(new PropPose {
                id = building.id,
                bx = anchor.birth.x,
                by = anchor.birth.y,
                x = building.transform.position.x,
                y = building.transform.position.y,
                rz = building.transform.eulerAngles.z,
                sx = scale.x,
                sy = scale.y,
            });
        }
        foreach (KeyValuePair<Body, ScavPlayer> pair in ScavPlayer.BodyToPlayerDict) {
            Body body = pair.Key;
            ScavPlayer plr = pair.Value;
            if (body == null || plr == null)
                continue;
            string pid = plr.GetPersistentId();
            if (string.IsNullOrEmpty(pid))
                continue;
            Vector3 scale = body.transform.localScale;
            dto.players.Add(new PlayerPose {
                id = pid,
                x = body.transform.position.x,
                y = body.transform.position.y,
                rz = body.transform.eulerAngles.z,
                sx = scale.x,
                sy = scale.y,
            });
        }
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), JsonConvert.SerializeObject(dto));
        log.l($"Debug map poses: {dto.props.Count} props, {dto.players.Count} players");
    }

    internal static void Read(string folder) {
        Clear();
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            return;
        FileDto dto = JsonConvert.DeserializeObject<FileDto>(File.ReadAllText(path));
        if (dto == null)
            return;
        if (dto.props != null)
            props.AddRange(dto.props);
        if (dto.players != null) {
            for (int i = 0; i < dto.players.Count; i++) {
                PlayerPose pose = dto.players[i];
                if (pose != null && !string.IsNullOrEmpty(pose.id))
                    players[pose.id] = pose;
            }
        }
        log.l($"Debug map poses loaded: {props.Count} props, {players.Count} players");
    }

    internal static void ApplyProps() {
        if (Net.IsClient || !SavesystemPatch.InDebugMap || props.Count == 0)
            return;
        var byId = new Dictionary<string, List<BuildingEntity>>();
        BuildingEntity[] buildings = Object.FindObjectsOfType<BuildingEntity>(true);
        for (int i = 0; i < buildings.Length; i++) {
            BuildingEntity building = buildings[i];
            if (building == null || string.IsNullOrEmpty(building.id))
                continue;
            if (!byId.TryGetValue(building.id, out List<BuildingEntity> list)) {
                list = new List<BuildingEntity>();
                byId[building.id] = list;
            }
            list.Add(building);
        }
        int placed = 0;
        for (int i = 0; i < props.Count; i++) {
            PropPose pose = props[i];
            if (pose == null || string.IsNullOrEmpty(pose.id) || !byId.TryGetValue(pose.id, out List<BuildingEntity> list))
                continue;
            BuildingEntity best = null;
            float bestD = 4f;
            Vector2 birth = new Vector2(pose.bx, pose.by);
            if (list.Count == 1)
                best = list[0];
            else {
                for (int n = 0; n < list.Count; n++) {
                    BuildingEntity building = list[n];
                    if (building == null)
                        continue;
                    float d = Vector2.Distance(building.transform.position, birth);
                    if (d < bestD) {
                        bestD = d;
                        best = building;
                    }
                }
            }
            if (best == null)
                continue;
            list.Remove(best);
            PlaceProp(best, pose, birth);
            placed++;
        }
        log.l($"Debug map poses applied to {placed} props");
    }

    internal static bool TryApply(Body body) {
        if (Net.IsClient || !SavesystemPatch.InDebugMap || body == null)
            return false;
        if (!body.TryGetNetBody(out NetBody nb) || nb == null || nb.plr == null)
            return false;
        string pid = nb.plr.GetPersistentId();
        if (string.IsNullOrEmpty(pid) || !players.TryGetValue(pid, out PlayerPose pose))
            return false;
        Transform t = body.transform;
        float sign = pose.sx < 0f ? -1f : 1f;
        MoveTool.NoteOriginal(t, new Vector3(sign, 1f, t.localScale.z));
        t.localScale = new Vector3(pose.sx, pose.sy, t.localScale.z);
        t.position = new Vector3(pose.x, pose.y, t.position.z);
        if (body.rb != null) {
            body.rb.velocity = Vector2.zero;
            body.rb.angularVelocity = 0f;
            body.rb.position = t.position;
        }
        if (nb.syncBody != null)
            nb.syncBody.WriteBodyData();
        return true;
    }

    static void PlaceProp(BuildingEntity building, PropPose pose, Vector2 birth) {
        Transform t = building.transform;
        MapPoseAnchor anchor = building.GetComponent<MapPoseAnchor>();
        if (anchor == null)
            anchor = building.gameObject.AddComponent<MapPoseAnchor>();
        // still on the generated spot. remember that, then move to the saved pose.
        if (!anchor.set) {
            anchor.birth = birth;
            anchor.birthRot = t.eulerAngles.z;
            anchor.birthScale = t.localScale;
            anchor.set = true;
        }
        MoveTool.NoteOriginal(t, new Vector3(anchor.birthScale.x, anchor.birthScale.y, t.localScale.z));
        ScaledLights.Remember(t);
        t.localScale = new Vector3(pose.sx, pose.sy, t.localScale.z);
        ScaledLights.Apply(t);
        t.SetPositionAndRotation(new Vector2(pose.x, pose.y), Quaternion.Euler(0f, 0f, pose.rz));
        anchor.keepRot = true;
        anchor.rot = pose.rz;
        Rigidbody2D rb = building.GetComponent<Rigidbody2D>();
        if (rb != null) {
            rb.velocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.position = t.position;
            rb.rotation = pose.rz;
        }
        if (Net.IsServer && NetObjectRegistry.TryGetSyncInfo(building.gameObject, out SyncInfo si) && si != null && !si.IsDestroyed)
            si.WriteObjectIntoPacket();
    }

    static bool Moved(Transform t, MapPoseAnchor anchor) {
        Vector2 pos = t.position;
        if ((pos - anchor.birth).sqrMagnitude > 0.0025f)
            return true;
        if (Mathf.Abs(Mathf.DeltaAngle(t.eulerAngles.z, anchor.birthRot)) > 0.5f)
            return true;
        Vector2 scale = t.localScale;
        if (Mathf.Abs(scale.x - anchor.birthScale.x) > 0.01f || Mathf.Abs(scale.y - anchor.birthScale.y) > 0.01f)
            return true;
        return false;
    }
}
