using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together {
    // layer 9 dens, layer 10 turrets and the cargo pulse, a pour that vanishes,
    // a flashlight that falls through the floor, and bag timers that crawl.
    internal static class FindTheBugReports {
        const float Near = 70f;
        const float ScanGap = 2f;

        struct Gate {
            internal float Next;
            internal int Hash;
        }

        struct BagSample {
            internal float At;
            internal float Cond;
            internal float Trade;
            internal bool HaveTrade;
            internal int DecayCalls;
            internal float DecayDrop;
            internal int DecayAsleep;
        }

        static readonly Dictionary<int, Gate> Gates = new Dictionary<int, Gate>(64);
        static readonly Dictionary<int, BagSample> Bags = new Dictionary<int, BagSample>(32);
        static readonly List<Item> BagItems = new List<Item>(32);
        static readonly StringBuilder Sb = new StringBuilder(640);
        static float _nextScan;
        static int _layerSeen = int.MinValue;

        internal static void Tick() {
            if (!Net.IsRunning) {
                return;
            }
            int layer = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : -1;
            if (layer != _layerSeen) {
                _layerSeen = layer;
                Say("layer", "depth=" + layer + " side=" + Side());
            }
            if (Time.unscaledTime < _nextScan) {
                return;
            }
            _nextScan = Time.unscaledTime + ScanGap;
        }

        internal static void Say(string topic, string detail) {
            log.l("[findthebuginator] " + topic + " " + detail, false);
        }

        static bool Due(int key, string sig, float gap) {
            return Due(key, sig, gap, false);
        }

        static bool Due(int key, string sig, float gap, bool force) {
            int hash = sig != null ? sig.GetHashCode() : 0;
            float now = Time.unscaledTime;
            if (!force && Gates.TryGetValue(key, out Gate gate)) {
                if (gate.Hash == hash && now < gate.Next) {
                    return false;
                }
                if (gate.Hash != hash && now < gate.Next - gap + 0.5f) {
                    return false;
                }
            }
            if (Gates.Count > 500) {
                Gates.Clear();
            }
            Gates[key] = new Gate { Next = now + gap, Hash = hash };
            return true;
        }

        static string Side() {
            if (!Net.IsRunning) {
                return "off";
            }
            return Net.IsServer ? "host" : "client";
        }

        static Vector2 Here() {
            if (Util.TryGetLocalBody(out Body body) && body != null) {
                return body.transform.position;
            }
            if (Camera.main != null) {
                return Camera.main.transform.position;
            }
            return Vector2.zero;
        }

        static string Cam() {
            Vector2 p = Here();
            return p.x.ToString("0.0") + "," + p.y.ToString("0.0");
        }

        static float CamDist(Vector3 p) {
            return Vector2.Distance(Here(), p);
        }

        static string SyncOf(GameObject go) {
            if (go != null && NetObjectRegistry.TryGetSyncInfo(go, out SyncInfo si) && si != null) {
                return si.syncId.id.ToString();
            }
            return "none";
        }

        static string ParentOf(Component c) {
            if (c == null || c.transform.parent == null) {
                return "world";
            }
            return c.transform.parent.name;
        }

        static string ItemName(Item item) {
            if (item == null) {
                return "-";
            }
            if (!string.IsNullOrEmpty(item.id)) {
                return item.id;
            }
            return item.name;
        }

        static bool InBag(Item item) {
            return item != null && item.transform.parent != null && item.transform.parent.GetComponent<Container>() != null;
        }

        static void AppendSprites(Component root) {
            if (root == null) {
                Sb.Append(" sprites=0");
                return;
            }
            SpriteRenderer[] sprites = root.GetComponentsInChildren<SpriteRenderer>(true);
            int shown = 0;
            int painted = 0;
            for (int i = 0; i < sprites.Length; i++) {
                SpriteRenderer sprite = sprites[i];
                if (sprite == null) {
                    continue;
                }
                if (sprite.sprite != null) {
                    painted++;
                }
                if (sprite.enabled && sprite.gameObject.activeInHierarchy && sprite.color.a > 0.05f) {
                    shown++;
                }
            }
            Sb.Append(" sprites=").Append(shown).Append('/').Append(sprites.Length);
            Sb.Append(" painted=").Append(painted);
            if (sprites.Length > 0 && sprites[0] != null) {
                SpriteRenderer sprite = sprites[0];
                Sb.Append(" spr=").Append(sprite.sprite != null ? sprite.sprite.name : "null");
                Sb.Append(" en=").Append(sprite.enabled ? 1 : 0);
                Sb.Append(" a=").Append(sprite.color.a.ToString("0.00"));
                Sb.Append(" sort=").Append(sprite.sortingLayerName).Append(':').Append(sprite.sortingOrder);
                Sb.Append(" vis=").Append(sprite.isVisible ? 1 : 0);
            }
        }

        static void AppendWhere(Component c) {
            GameObject go = c.gameObject;
            Vector3 p = go.transform.position;
            Vector3 scale = go.transform.lossyScale;
            Sb.Append(" side=").Append(Side());
            Sb.Append(" name=").Append(go.name);
            Sb.Append(" active=").Append(go.activeInHierarchy ? 1 : 0);
            Sb.Append(" self=").Append(go.activeSelf ? 1 : 0);
            Sb.Append(" pos=").Append(p.x.ToString("0.0")).Append(',').Append(p.y.ToString("0.0"));
            Sb.Append(" scale=").Append(scale.x.ToString("0.00")).Append(',').Append(scale.y.ToString("0.00"));
            Sb.Append(" cam=").Append(CamDist(p).ToString("0.0"));
            Sb.Append(" sync=").Append(SyncOf(go));
            Sb.Append(" parent=").Append(ParentOf(c));
            AppendSprites(c);
        }

        static string Listener() {
            AudioListener listener = Camera.main != null ? Camera.main.GetComponent<AudioListener>() : null;
            return "listen=" + (listener != null && listener.enabled ? 1 : 0)
                + " vol=" + AudioListener.volume.ToString("0.00")
                + " pause=" + (AudioListener.pause ? 1 : 0);
        }

#if FULLGAME
        static void ScanSuckers(int layer) {
            BloodSuckerScript[] all = UnityEngine.Object.FindObjectsOfType<BloodSuckerScript>(true);
            if (all == null || all.Length == 0) {
                string none = "n=0 layer=" + layer + " side=" + Side() + " cam=" + Cam();
                if (Due(90009, none, 8f)) {
                    Say("bloodsucker", none);
                }
                return;
            }
            int near = 0;
            float nearest = 99999f;
            for (int i = 0; i < all.Length; i++) {
                BloodSuckerScript sucker = all[i];
                if (sucker == null) {
                    continue;
                }
                float dist = CamDist(sucker.transform.position);
                if (dist < nearest) {
                    nearest = dist;
                }
                if (dist > Near) {
                    continue;
                }
                near++;
                Sb.Clear();
                Sb.Append("layer=").Append(layer);
                AppendWhere(sucker);
                LineRenderer line = sucker.GetComponentInChildren<LineRenderer>(true);
                if (line == null) {
                    Sb.Append(" line=none");
                } else {
                    Sb.Append(" lineEn=").Append(line.enabled ? 1 : 0);
                    Sb.Append(" lineOn=").Append(line.gameObject.activeInHierarchy ? 1 : 0);
                    Sb.Append(" pts=").Append(line.positionCount);
                    Sb.Append(" width=").Append(line.widthMultiplier.ToString("0.00"));
                }
                string lineText = Sb.ToString();
                if (Due(sucker.GetInstanceID(), lineText, 8f)) {
                    Say("bloodsucker", lineText);
                }
            }
            if (near == 0) {
                string far = "n=" + all.Length + " near=0 nearest=" + nearest.ToString("0.0") + " side=" + Side() + " cam=" + Cam();
                if (Due(90008, far, 8f)) {
                    Say("bloodsucker", far);
                }
            }
        }
#endif

        static void ScanTurrets(int layer) {
            CeilingTurret[] all = UnityEngine.Object.FindObjectsOfType<CeilingTurret>(true);
            if (all == null || all.Length == 0) {
                string none = "n=0 layer=" + layer + " side=" + Side() + " cam=" + Cam() + " " + Listener();
                if (Due(90010, none, 8f)) {
                    Say("turret", none);
                }
                return;
            }
            int near = 0;
            float nearest = 99999f;
            for (int i = 0; i < all.Length; i++) {
                CeilingTurret turret = all[i];
                if (turret == null) {
                    continue;
                }
                float dist = CamDist(turret.transform.position);
                if (dist < nearest) {
                    nearest = dist;
                }
                if (dist > Near) {
                    continue;
                }
                near++;
                string lineText = TurretLine(turret, layer);
                if (Due(turret.GetInstanceID(), lineText, 8f)) {
                    Say("turret", lineText);
                }
            }
            if (near == 0) {
                string far = "n=" + all.Length + " near=0 nearest=" + nearest.ToString("0.0") + " side=" + Side() + " cam=" + Cam();
                if (Due(90011, far, 8f)) {
                    Say("turret", far);
                }
            }
        }

        static string TurretLine(CeilingTurret turret, int layer) {
            Sb.Clear();
            Sb.Append("layer=").Append(layer);
            AppendWhere(turret);
            Sb.Append(" always=").Append(turret.alwaysSee ? 1 : 0);
            AppendGun(turret.upperGun, "up");
            AppendGun(turret.lowerGun, "low");
            if (turret.lights == null) {
                Sb.Append(" lamp=none");
            } else {
                Sb.Append(" lamp=").Append(turret.lights.enabled ? 1 : 0);
                Sb.Append(" lampVis=").Append(turret.lights.isVisible ? 1 : 0);
            }
            CeilingTurretClient heard = turret.GetComponent<CeilingTurretClient>();
            if (heard == null) {
                Sb.Append(" heard=0");
            } else {
                Sb.Append(" heard=1 see=").Append(heard.Seeing ? 1 : 0);
                Sb.Append(" saw=").Append(heard.TimeSaw.ToString("0.00"));
            }
            AppendAudio(turret.source);
            Sb.Append(' ').Append(Listener());
            return Sb.ToString();
        }

        static void AppendGun(Transform gun, string label) {
            if (gun == null) {
                Sb.Append(' ').Append(label).Append("=none");
                return;
            }
            Vector3 lp = gun.localPosition;
            Sb.Append(' ').Append(label).Append('=').Append(lp.x.ToString("0.00")).Append(',').Append(lp.y.ToString("0.00"));
            Sb.Append(" on=").Append(gun.gameObject.activeInHierarchy ? 1 : 0);
            SpriteRenderer sprite = gun.GetComponent<SpriteRenderer>();
            if (sprite == null) {
                sprite = gun.GetComponentInChildren<SpriteRenderer>(true);
            }
            if (sprite == null) {
                Sb.Append(" spr=none");
                return;
            }
            Sb.Append(" spr=").Append(sprite.sprite != null ? sprite.sprite.name : "null");
            Sb.Append(" en=").Append(sprite.enabled ? 1 : 0);
            Sb.Append(" a=").Append(sprite.color.a.ToString("0.00"));
            Sb.Append(" vis=").Append(sprite.isVisible ? 1 : 0);
            Sb.Append(" sort=").Append(sprite.sortingLayerName).Append(':').Append(sprite.sortingOrder);
        }

        static void AppendAudio(AudioSource source) {
            if (source == null) {
                Sb.Append(" audio=none");
                return;
            }
            Sb.Append(" vol=").Append(source.volume.ToString("0.00"));
            Sb.Append(" play=").Append(source.isPlaying ? 1 : 0);
            Sb.Append(" mute=").Append(source.mute ? 1 : 0);
            Sb.Append(" audioEn=").Append(source.enabled ? 1 : 0);
            Sb.Append(" clip=").Append(source.clip != null ? source.clip.name : "null");
            Sb.Append(" blend=").Append(source.spatialBlend.ToString("0.00"));
            Sb.Append(" range=").Append(source.minDistance.ToString("0")).Append('-').Append(source.maxDistance.ToString("0"));
        }

        internal static void NoteTurretMiss(ushort syncId, Vector2 pos, bool seeing, bool fired) {
            string line = "packet-miss side=client sync=" + syncId
                + " fired=" + (fired ? 1 : 0)
                + " see=" + (seeing ? 1 : 0)
                + " pos=" + pos.x.ToString("0.0") + "," + pos.y.ToString("0.0")
                + " cam=" + Cam();
            if (Due(90012 ^ syncId, line, 2f, fired)) {
                Say("turret", line);
            }
        }

        internal static void NoteTurretBang(CeilingTurret turret, Vector2 shotPos, bool seeing) {
            if (turret == null) {
                Say("turret", "bang mesh=0 side=client see=" + (seeing ? 1 : 0)
                    + " shot=" + shotPos.x.ToString("0.0") + "," + shotPos.y.ToString("0.0")
                    + " cam=" + Cam() + " " + Listener());
                return;
            }
            int layer = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : -1;
            Say("turret", "bang mesh=1 see=" + (seeing ? 1 : 0)
                + " shot=" + shotPos.x.ToString("0.0") + "," + shotPos.y.ToString("0.0")
                + " " + TurretLine(turret, layer));
        }

        static void ScanPulse(int layer) {
            BlueHeart[] all = UnityEngine.Object.FindObjectsOfType<BlueHeart>(true);
            if (all == null || all.Length == 0) {
                string none = "n=0 layer=" + layer + " side=" + Side() + " cam=" + Cam() + " " + BlueHeart_MultiplayerPatch.PulseClock();
                if (Due(90020, none, 8f)) {
                    Say("pulse", none);
                }
                return;
            }
            for (int i = 0; i < all.Length; i++) {
                BlueHeart heart = all[i];
                if (heart == null) {
                    continue;
                }
                Sb.Clear();
                Sb.Append("layer=").Append(layer);
                AppendWhere(heart);
                Sb.Append(' ').Append(BlueHeart_MultiplayerPatch.PulseClock());
                if (heart.mat == null) {
                    Sb.Append(" mat=none");
                } else {
                    Sb.Append(" shader=").Append(heart.mat.shader != null ? heart.mat.shader.name : "null");
                    if (heart.mat.HasProperty("_Color")) {
                        Color color = heart.mat.GetColor("_Color");
                        Sb.Append(" rgba=").Append(color.r.ToString("0.00")).Append(',').Append(color.g.ToString("0.00")).Append(',').Append(color.b.ToString("0.00")).Append(',').Append(color.a.ToString("0.00"));
                    }
                    if (heart.mat.HasProperty("_DesiredDistance")) {
                        Sb.Append(" dist=").Append(heart.mat.GetFloat("_DesiredDistance").ToString("0.00"));
                    }
                    if (heart.mat.HasProperty("_EdgeSize")) {
                        Sb.Append(" edge=").Append(heart.mat.GetFloat("_EdgeSize").ToString("0.00"));
                    }
                }
                Renderer[] renderers = heart.GetComponentsInChildren<Renderer>(true);
                int shown = 0;
                for (int r = 0; r < renderers.Length; r++) {
                    Renderer renderer = renderers[r];
                    if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy) {
                        shown++;
                    }
                }
                Sb.Append(" rends=").Append(shown).Append('/').Append(renderers.Length);
                Light2D light = heart.GetComponent<Light2D>();
                if (light == null) {
                    Sb.Append(" light=none");
                } else {
                    Sb.Append(" light=").Append(light.enabled ? 1 : 0);
                    Sb.Append(" lint=").Append(light.intensity.ToString("0.00"));
                }
                string lineText = Sb.ToString();
                if (Due(heart.GetInstanceID(), lineText, 8f)) {
                    Say("pulse", lineText);
                }
            }
        }

        static string Box(WaterContainerItem box, string label) {
            Item item = box != null ? box.GetComponent<Item>() : null;
            int n = box != null && box.stack != null ? box.stack.Count : 0;
            float total = box != null ? box.CurrentTotal : -1f;
            return label + "=" + ItemName(item)
                + "#" + (item != null ? SyncOf(item.gameObject) : "none")
                + " ml=" + total.ToString("0.0")
                + " n=" + n
                + " parent=" + ParentOf(item);
        }

        internal static void NoteAdd(WaterContainerItem box, string liquidId, float asked, float got) {
            if (!Net.IsRunning || box == null) {
                return;
            }
            Item item = box.GetComponent<Item>();
            bool reject = asked > 0f && got <= 0f;
            bool empty = box.CurrentTotal <= 0.01f;
            string line = "add side=" + Side()
                + " " + Box(box, "box")
                + " liquid=" + (string.IsNullOrEmpty(liquidId) ? "-" : liquidId)
                + " asked=" + asked.ToString("0.0")
                + " got=" + got.ToString("0.0")
                + " space=" + box.SpaceLeft.ToString("0.0")
                + " cap=" + box.Capacity.ToString("0.0")
                + " active=" + (item != null && item.gameObject.activeInHierarchy ? 1 : 0);
            if (Due(box.GetInstanceID() ^ 0x11A0, line, 1.5f, reject || empty)) {
                Say("liquid", line);
            }
        }

        internal static void NotePourStart(WaterContainerItem dest, WaterContainerItem src) {
            if (!Net.IsRunning) {
                return;
            }
            Say("liquid", "start side=" + Side() + " " + Box(dest, "dest") + " " + Box(src, "src"));
        }

        internal static void NotePourSend(Item dest, Item src, float ml) {
            if (!Net.IsRunning) {
                return;
            }
            WaterContainerItem to = dest != null ? dest.GetComponent<WaterContainerItem>() : null;
            WaterContainerItem from = src != null ? src.GetComponent<WaterContainerItem>() : null;
            Say("liquid", "send side=" + Side() + " ml=" + ml.ToString("0.0") + " " + Box(to, "dest") + " " + Box(from, "src"));
        }

        internal static void NotePourDone(WaterContainerItem dest, WaterContainerItem src, float ml) {
            if (!Net.IsRunning) {
                return;
            }
            Say("liquid", "finish side=" + Side() + " ml=" + ml.ToString("0.0") + " " + Box(dest, "dest") + " " + Box(src, "src"));
        }

        internal static void NoteCombine(string why, float asked, SyncInfo to, SyncInfo from, float toBefore, float fromBefore) {
            if (!Net.IsRunning) {
                return;
            }
            WaterContainerItem dest = to != null ? to.liquidcontainer : null;
            WaterContainerItem src = from != null ? from.liquidcontainer : null;
            string line = "combine " + why
                + " side=" + Side()
                + " asked=" + asked.ToString("0.0")
                + " " + Box(dest, "dest")
                + " before=" + toBefore.ToString("0.0")
                + " " + Box(src, "src")
                + " before=" + fromBefore.ToString("0.0");
            Say("liquid", line);
        }

        internal static void NoteLiquidHold(WaterContainerItem box, int syncedN) {
            if (!Net.IsRunning || box == null) {
                return;
            }
            float local = box.CurrentTotal;
            if (local <= 0.01f && syncedN == 0) {
                return;
            }
            bool clash = local > 1f && syncedN == 0;
            string line = "hold side=" + Side() + " " + Box(box, "box") + " syncedN=" + syncedN;
            if (Due(box.GetInstanceID() ^ 0x220, line, 2f, clash)) {
                Say("liquid", line);
            }
        }

        internal static void NoteLiquidApply(WaterContainerItem box, float syncedMl, int syncedN) {
            if (!Net.IsRunning || box == null) {
                return;
            }
            float local = box.CurrentTotal;
            int localN = box.stack != null ? box.stack.Count : 0;
            bool wipe = local > 1f && syncedMl <= 0.01f;
            bool appear = local <= 0.01f && syncedMl > 1f;
            if (!wipe && !appear && Mathf.Abs(local - syncedMl) < 5f) {
                return;
            }
            string line = "apply side=" + Side()
                + " " + Box(box, "box")
                + " localN=" + localN
                + " synced=" + syncedMl.ToString("0.0")
                + " syncedN=" + syncedN
                + (wipe ? " WIPE" : "")
                + (appear ? " FILL" : "");
            if (Due(box.GetInstanceID() ^ 0x221, line, 2f, wipe)) {
                Say("liquid", line);
            }
        }

        internal static void NoteFlash(Item item, string why) {
            if (!Net.IsRunning || item == null) {
                return;
            }
            if (item.id != "flashlight" && item.id != "emergencylight") {
                return;
            }
            BatteryItem battery = item.battery;
            bool dead = item.condition <= 0.05f || battery == null || !battery.hasBattery;
            if (!dead) {
                return;
            }
            if (item.transform.parent == null && CamDist(item.transform.position) > 25f) {
                return;
            }
            Sb.Clear();
            Sb.Append(why);
            Sb.Append(" side=").Append(Side());
            Sb.Append(" id=").Append(ItemName(item));
            Sb.Append(" sync=").Append(SyncOf(item.gameObject));
            Sb.Append(" cond=").Append(item.condition.ToString("0.000"));
            Sb.Append(" hasBat=").Append(battery != null && battery.hasBattery ? 1 : 0);
            Sb.Append(" hasCharge=").Append(item.condition > 0f ? 1 : 0);
            if (battery != null) {
                float charge = battery.hasBattery ? item.condition * battery.maxCharge : 0f;
                Sb.Append(" charge=").Append(charge.ToString("0.00"));
                Sb.Append(" max=").Append(battery.maxCharge.ToString("0.00"));
                Sb.Append(" type=").Append(string.IsNullOrEmpty(battery.batteryType) ? "-" : battery.batteryType);
            }
            CustomItemBehaviour mode = item.GetComponent<CustomItemBehaviour>();
            Sb.Append(" state=").Append(mode != null ? mode.state.ToString() : "-");
            Sb.Append(" active=").Append(item.gameObject.activeInHierarchy ? 1 : 0);
            Sb.Append(" self=").Append(item.gameObject.activeSelf ? 1 : 0);
            Sb.Append(" parent=").Append(ParentOf(item));
            Sb.Append(" ulayer=").Append(LayerMask.LayerToName(item.gameObject.layer));
            Vector3 p = item.transform.position;
            Sb.Append(" pos=").Append(p.x.ToString("0.0")).Append(',').Append(p.y.ToString("0.0"));
            Rigidbody2D rb = item.GetComponent<Rigidbody2D>();
            if (rb == null) {
                Sb.Append(" rb=none");
            } else {
                Sb.Append(" body=").Append(rb.bodyType);
                Sb.Append(" sim=").Append(rb.simulated ? 1 : 0);
                Sb.Append(" kin=").Append(rb.isKinematic ? 1 : 0);
                Sb.Append(" grav=").Append(rb.gravityScale.ToString("0.00"));
                Sb.Append(" vel=").Append(rb.velocity.x.ToString("0.00")).Append(',').Append(rb.velocity.y.ToString("0.00"));
                Sb.Append(" detect=").Append(rb.collisionDetectionMode);
            }
            Collider2D[] cols = item.GetComponentsInChildren<Collider2D>(true);
            int on = 0;
            int triggers = 0;
            for (int i = 0; i < cols.Length; i++) {
                if (cols[i] == null) {
                    continue;
                }
                if (cols[i].enabled && cols[i].gameObject.activeInHierarchy) {
                    on++;
                }
                if (cols[i].isTrigger) {
                    triggers++;
                }
            }
            Sb.Append(" col=").Append(on).Append('/').Append(cols.Length);
            Sb.Append(" trig=").Append(triggers);
            if (item.transform.childCount > 0) {
                Light2D beam = item.transform.GetChild(0).GetComponent<Light2D>();
                if (beam != null) {
                    Sb.Append(" beam=").Append(beam.enabled ? 1 : 0);
                    Sb.Append(" bint=").Append(beam.intensity.ToString("0.00"));
                }
            }
            string line = Sb.ToString();
            if (Due(item.GetInstanceID() ^ 0x51A5, line, 1f)) {
                Say("flashlight", line);
            }
        }

        internal static void NoteDecay(Item item) {
            if (!Net.IsRunning || item == null || !InBag(item)) {
                return;
            }
            if (item.battery == null && item.GetComponent<BoughtItem>() == null) {
                return;
            }
            int id = item.GetInstanceID();
            if (!Bags.TryGetValue(id, out BagSample sample)) {
                sample = new BagSample();
            }
            sample.DecayCalls++;
            if (!item.gameObject.activeSelf) {
                sample.DecayAsleep++;
            }
            Bags[id] = sample;
        }

        static void ScanBags() {
            BagItems.Clear();
            BoughtItem[] bought = UnityEngine.Object.FindObjectsOfType<BoughtItem>(true);
            if (bought != null) {
                for (int i = 0; i < bought.Length; i++) {
                    Item item = bought[i] != null ? bought[i].GetComponent<Item>() : null;
                    if (item != null && InBag(item)) {
                        BagItems.Add(item);
                    }
                }
            }
            BatteryItem[] batteries = UnityEngine.Object.FindObjectsOfType<BatteryItem>(true);
            if (batteries != null) {
                for (int i = 0; i < batteries.Length; i++) {
                    Item item = batteries[i] != null ? batteries[i].GetComponent<Item>() : null;
                    if (item != null && InBag(item) && !BagItems.Contains(item)) {
                        BagItems.Add(item);
                    }
                }
            }
            int logged = 0;
            int slow = 0;
            for (int pass = 0; pass < 2 && logged < 24; pass++) {
                for (int i = 0; i < BagItems.Count && logged < 24; i++) {
                    Item item = BagItems[i];
                    if (item == null) {
                        continue;
                    }
                    bool hot = !item.gameObject.activeSelf || BagLooksSlow(item);
                    if (pass == 0 && hot) {
                        slow++;
                    }
                    if ((pass == 0) != hot) {
                        continue;
                    }
                    if (LogBag(item)) {
                        logged++;
                    }
                }
            }
            if (BagItems.Count > logged) {
                string extra = "tracked=" + BagItems.Count + " logged=" + logged + " slow=" + slow + " side=" + Side();
                if (Due(90030, extra, 8f)) {
                    Say("bag", extra);
                }
            }
            if (Bags.Count > 400) {
                Bags.Clear();
            }
        }

        static bool BagLooksSlow(Item item) {
            if (!Bags.TryGetValue(item.GetInstanceID(), out BagSample sample) || sample.At <= 0f) {
                return !item.gameObject.activeSelf;
            }
            float dt = Time.unscaledTime - sample.At;
            if (dt < 1f) {
                return false;
            }
            BoughtItem bought = item.GetComponent<BoughtItem>();
            if (bought != null && sample.HaveTrade && sample.Trade - bought.time < dt * 0.5f) {
                return true;
            }
            if (item.battery != null && item.battery.hasBattery && BatteryDecay(item) && sample.Cond - item.condition < 0.0001f) {
                return true;
            }
            return false;
        }

        static bool BatteryDecay(Item item) {
            return item.Stats != null && (item.Stats.decayInfo & (byte)ItemInfo.DecayType.BatteryDecay) != 0;
        }

        static bool LogBag(Item item) {
            int id = item.GetInstanceID();
            BoughtItem bought = item.GetComponent<BoughtItem>();
            float now = Time.unscaledTime;
            Bags.TryGetValue(id, out BagSample sample);
            float dt = sample.At > 0f ? now - sample.At : 0f;
            float dCond = sample.At > 0f ? sample.Cond - item.condition : 0f;
            float dTrade = bought != null && sample.HaveTrade ? sample.Trade - bought.time : 0f;
            bool open = false;
            PlayerCamera cam = PlayerCamera.main;
            Container box = item.transform.parent != null ? item.transform.parent.GetComponent<Container>() : null;
            if (cam != null && box != null && cam.currentContainer == box) {
                open = true;
            }
            string syncTrade = "-";
            string syncId = SyncOf(item.gameObject);
            if (NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo si) && si != null) {
                syncTrade = si.boughtitemtime.ToString("0.0");
            }
            Sb.Clear();
            Sb.Append("side=").Append(Side());
            Sb.Append(" id=").Append(ItemName(item));
            Sb.Append(" sync=").Append(syncId);
            Sb.Append(" active=").Append(item.gameObject.activeSelf ? 1 : 0);
            Sb.Append(" hier=").Append(item.gameObject.activeInHierarchy ? 1 : 0);
            Sb.Append(" open=").Append(open ? 1 : 0);
            Sb.Append(" parent=").Append(ParentOf(item));
            Sb.Append(" cond=").Append(item.condition.ToString("0.000"));
            Sb.Append(" dt=").Append(dt.ToString("0.00"));
            Sb.Append(" dCond=").Append(dCond.ToString("0.000"));
            Sb.Append(" decayCalls=").Append(sample.DecayCalls);
            Sb.Append(" decayAsleep=").Append(sample.DecayAsleep);
            Sb.Append(" batDecay=").Append(BatteryDecay(item) ? 1 : 0);
            BatteryItem battery = item.battery;
            if (battery == null) {
                Sb.Append(" bat=none");
            } else {
                Sb.Append(" hasBat=").Append(battery.hasBattery ? 1 : 0);
                Sb.Append(" hasCharge=").Append(item.condition > 0f ? 1 : 0);
                float charge = battery.hasBattery ? item.condition * battery.maxCharge : 0f;
                Sb.Append(" charge=").Append(charge.ToString("0.00"));
            }
            if (bought == null) {
                Sb.Append(" trade=none");
            } else {
                Sb.Append(" trade=").Append(bought.time.ToString("0.00"));
                Sb.Append(" dTrade=").Append(dTrade.ToString("0.00"));
                Sb.Append(" syncTrade=").Append(syncTrade);
            }
            string line = Sb.ToString();
            string sig = (item.gameObject.activeSelf ? "1" : "0")
                + (open ? "o" : "c")
                + Mathf.RoundToInt(item.condition * 20f).ToString()
                + ":" + (bought != null ? Mathf.FloorToInt(bought.time / 5f).ToString() : "-")
                + ":" + (item.condition > 0f ? "1" : "0");
            sample.At = now;
            sample.Cond = item.condition;
            sample.HaveTrade = bought != null;
            sample.Trade = bought != null ? bought.time : 0f;
            sample.DecayCalls = 0;
            sample.DecayDrop = 0f;
            sample.DecayAsleep = 0;
            Bags[id] = sample;
            if (!Due(id ^ 0x6A6, sig, 8f)) {
                return false;
            }
            Say("bag", line);
            return true;
        }
    }

    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.StartLiquidTransfer))]
    static class FindTheBug_StartPour {
        static void Postfix(WaterContainerItem wat1, WaterContainerItem wat2) {
            FindTheBugReports.NotePourStart(wat1, wat2);
        }
    }

    [HarmonyPatch(typeof(LiquidTransfer), nameof(LiquidTransfer.Finish))]
    static class FindTheBug_PourDone {
        static void Postfix(LiquidTransfer __instance) {
            if (__instance == null) {
                return;
            }
            FindTheBugReports.NotePourDone(__instance.transferTo, __instance.transferFrom, __instance.ml);
        }
    }

    [HarmonyPatch(typeof(Item), nameof(Item.HandleDecay))]
    static class FindTheBug_Decay {
        static void Postfix(Item __instance) {
            FindTheBugReports.NoteDecay(__instance);
        }
    }

    [HarmonyPatch(typeof(BatteryItem), "Update")]
    static class FindTheBug_Battery {
        static void Postfix(BatteryItem __instance) {
            if (__instance == null) {
                return;
            }
            Item item = __instance.GetComponent<Item>();
            FindTheBugReports.NoteFlash(item, "battery-update");
        }
    }

    // a spawn that returns with the owner already gone is a leak. a refused spawn is the fix holding.
    [HarmonyPatch(typeof(SyncInfo), nameof(SyncInfo.CreateObject))]
    static class FindTheBug_LeaveSpawn {
        static void Postfix(SyncInfo __instance, bool __result) {
            // a mesh that was already there is not a new spawn. only a refused create counts.
            if (__result || __instance == null || !LeaveGear.OwnerMissing(__instance)) {
                return;
            }
            LeaveGear.MarkBlock(__instance);
        }
    }

    // host release line, or the client's orphan count at the moment the player object dies.
    [HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.OnDestroy))]
    static class FindTheBug_LeaveBody {
        static void Postfix(ScavPlayer __instance) {
            string line = LeaveGear.TakeReleaseLine();
            if (line != null) {
                FindTheBugReports.Say("leavegear", line);
                return;
            }
            if (__instance == null) {
                return;
            }
            FindTheBugReports.Say("leavegear", "observe side=" + (Net.IsServer ? "host" : "client")
                + " player=" + (__instance.playerName ?? "-")
                + " local=" + (__instance.IsLocal ? 1 : 0)
                + " orphans=" + LeaveGear.OrphanCount());
        }
    }
}
