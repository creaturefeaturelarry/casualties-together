using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace Together;





public class CharStatusVisuals : MonoBehaviour {
    internal static GameObject PREFAB_ExpTalkText = null;
    internal static Type[] ALL_STATUS_ICONS = null;


    internal static bool DEV_forceenable_nametags_for_everyone =
#if RELEASE
        false
#else
        true
#endif
        ;

    public GameObject nametag { get; private set; }
    public Body body => GetComponent<Body>();
    public NetBody netbody => GetComponent<NetBody>();
    public ScavPlayer plr => netbody?.plr;
    public GameObject chara => netbody.chara;
    public Color color => netbody.color;
    public Color color_st = Color.white;
    public new string name => netbody.bodyName;
    public int total_visible_icons = 0;

    public Vector2 body_center_pos = Vector2.zero;   


    public class IconInstance {
        public NetBody npc;
        public SpriteRenderer spr => icon.GetComponent<SpriteRenderer>();
        public GameObject icon;
        public ICharacterStatusIcon iconfunc;
        public int index;
        public float xoverlapmove = 0;

        public Vector2 position {
            get {
                return icon.transform.position;
            }
            set {
                icon.transform.position =
                                new Vector3(
                                    value.x,
                                    value.y,
                                    npc.body.transform.position.z
                                );
            }
        }
    }


    Dictionary<Type, IconInstance> icons = new();






    void Awake() {
        if (ALL_STATUS_ICONS == null) {
            ALL_STATUS_ICONS = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypesSafely()).Where(myType => myType.IsClass && !myType.IsAbstract && !myType.IsInterface && typeof(ICharacterStatusIcon).IsAssignableFrom(myType)).ToArray();
        }
        if (PREFAB_ExpTalkText == null) {
            PREFAB_ExpTalkText = Resources.Load("special/ExpTalkText") as GameObject;
            DontDestroyOnLoad(PREFAB_ExpTalkText);
            PREFAB_ExpTalkText.SetActive(false);
        }

        var initposc3 = (netbody.GetHeadPos() + Vector2.up * 2);
        for (int i = 0; i < ALL_STATUS_ICONS.Length; i++) {
            var item = ALL_STATUS_ICONS[i];
            var icon = (ICharacterStatusIcon)Activator.CreateInstance(item);

            var icongo = new GameObject(item.Name);

            try {
                icongo.transform.parent = netbody.chara.transform;
                var spr = icongo.AddComponent<SpriteRenderer>();
                spr.sortingOrder = 6000;
                spr.color = color_st;
                icongo.transform.localScale = Vector3.one * 9f;

                icon.Create(this, spr);

                var icinst = new IconInstance {
                    npc = this.netbody,
                    icon = icongo,
                    iconfunc = icon,
                    index = i,
                };
                icinst.position = initposc3;

                icongo.SetActive(false);
                icons.Add(item, icinst);
            } catch (Exception e) {
                log.error(e);
                if (icongo != null)
                    GameObject.Destroy(icongo);
            } finally {

            }
        }

        {
            nametag = UnityEngine.Object.Instantiate(PREFAB_ExpTalkText, transform.position, Quaternion.identity);
            nametag.SetActive(true);
            nametag.transform.SetParent(body.transform.parent);
            var tmp = nametag.GetComponent<TextMeshPro>();
            tmp.rectTransform.position += new Vector3(0, 99999);
            tmp.richText = false;


        }

    }
    void Start() {
        if (plr != null ) {
            CharStatusVisuals.TryMakeNametagFancy(nametag.GetComponent<TextMeshPro>(), plr);
        }
    }
    public static void TryMakeNametagFancy(TMP_Text tmp, ScavPlayer plr) {
        if (KnownPersons.CanDoFancyNametagFor(plr)) {
            CharStatusVisuals.MakeNametagFancy(tmp, plr);
        }
    }
    public static void MakeNametagFancy(TMP_Text tmp, ScavPlayer plr) {
        if (tmp.fontMaterial != null && tmp.fontMaterial.name.StartsWith("FANCY")) {
            return; // already fancy
        }

        tmp.fontMaterial = new Material(tmp.fontMaterial);
        tmp.fontMaterial.name = "FANCYTEXT";
        tmp.fontMaterial.shader = CoopModAssets.coolshader;
        tmp.fontMaterial.SetFloat("_OutlineWidth", tmp.outlineWidth);

        if (plr != null) {
            FancyNametagUpdateColor(tmp, plr.playerColor);
        }
    }
    public static bool FancyNametagUpdateColor(TMP_Text tmp, Color24 color, float scale = 0.9f) {
        if (tmp.fontMaterial.name.StartsWith("FANCY")) {
            tmp.color = Color.white * 1.2f;

            var colora = color;
            var colorb = Color.Lerp(((Color)color).ColorBW(), color, 0.3f);

            tmp.fontMaterial.SetColor("_ColorA", colorb);
            tmp.fontMaterial.SetColor("_ColorB", colora);
            tmp.fontMaterial.SetFloat("_LocalGradientScale", scale);
            tmp.fontMaterial.SetFloat("_GradientSpeed", 6f);
            return true;
        } 
        return false;
    }


    protected void LateUpdate() {

        // get average pos
        {
            this.body_center_pos = Vector2.zero;
            Limb[] limbs = body.limbs;
            foreach (Limb limb in limbs) {
                this.body_center_pos += (Vector2)limb.transform.position;
            }
            this.body_center_pos /= (float)body.limbs.Length;
            if (body.standing) {
                this.body_center_pos.x = body.transform.position.x;
            }
        }


        if (nametag != null) {

            if (DEV_forceenable_nametags_for_everyone) {
                nametag.SetActive(true);
            } else if (body.alive && !Util.IsBodyLocal(body)) {

                var mousefocus = Multiplayer.rules.EnableNametags
                    && !(Multiplayer.rules.UnchippedHideNametags && Util.IsUnchipped())
                    ;

                if (nametag.activeSelf != mousefocus) {
                    nametag.SetActive(mousefocus);
                }

            } else if (nametag.activeSelf) {
                nametag.SetActive(false);
            }
            if (nametag.activeSelf) {
                var tmp = nametag.GetComponent<TextMeshPro>();
                tmp.rectTransform.position = (this.body_center_pos - Vector2.up * 3f);

            }
        }




        int visiblecount = 0;

        if (Con._DEV_HIDEHUD || !Multiplayer.rules.EnableStatusIcons) {
            foreach (var item in icons.Values) {
                item.icon.SetActive(false);
            }
        } else {

            Vector2 headpos = netbody.GetHeadPos();
            const float cooloffset = 5.5f;


            var tmp_tmp = body.talker.text;
            var tmp_topworld = tmp_tmp.transform.TransformPoint(new Vector3(tmp_tmp.bounds.center.x, tmp_tmp.bounds.max.y, tmp_tmp.bounds.center.z));

            foreach (var item in icons.Values) {

                if (!item.icon.activeSelf) {
                    if (body.alive && item.iconfunc.AppearCondition()) {
                        item.icon.SetActive(true);
                        total_visible_icons++;
                        if (!body.IsBodyLocal())
                            Util.PlayWorldSoundOnScreenIfInRange("miniClick", body_center_pos, 0.07f, 1f, 20);
                    } else {
                        continue;
                    }
                } else {
                    if (item.iconfunc.DisappearCondition()) {
                        item.icon.SetActive(false);
                        total_visible_icons--;
                        if (!body.IsBodyLocal())
                            Util.PlayWorldSoundOnScreenIfInRange("miniClick", body_center_pos, 0.07f, 0.6f, 20);
                        continue;
                    }
                }

                var curpos = item.position;

                const float angle_used_per_icon = 0.646f;
                var full_angle = (float)(total_visible_icons - 1) * angle_used_per_icon;
                item.xoverlapmove = Mathf.Lerp(item.xoverlapmove, -full_angle * 0.5f + (float)visiblecount * angle_used_per_icon, Time.deltaTime * 10f);
                var targetpos = body_center_pos + KM.AngleToVector2(item.xoverlapmove) * cooloffset;

                if (!String.IsNullOrEmpty(body.talker.text.text)) {
                    targetpos.y = Mathf.Max(targetpos.y, tmp_topworld.y + 1.5f);
                }

                item.position = KM.ClampDistance(Vector2.Lerp(curpos, targetpos, Time.deltaTime * 30f), targetpos, 2);

                item.iconfunc.Update();

                visiblecount++;
            }
        }
        total_visible_icons = visiblecount;





    }






    public void ApplyNameAndColor() {
        

        color_st = new Color(color.r, color.g, color.b, 0.87f);
        if (nametag != null) {
        
            var tmp = nametag.GetComponent<TextMeshPro>();
            tmp.name = "CharacterNametag_" + name;
            tmp.text = name;

            if (!FancyNametagUpdateColor(tmp, color_st)) {
                tmp.color = color_st;
            }
        }

        if (
            !netbody.IsLocal //&& Multiplayer.rules.ColoredTalker
        ) {
            var idkiftheresdifferentalphaornot = new Color(color.r, color.g, color.b, body.talker.text.color.a);
            body.talker.text.color = idkiftheresdifferentalphaornot;
        }


        foreach (var item in icons.Values) {
            item.spr.color = color_st;
        }

        if (netbody.IsPlayer) {
            if (netbody.plr.locationPingCircle) {
                netbody.plr.locationPingCircle.name = ("fingerpointercircle_" + name);
                var spr = netbody.plr.locationPingCircle.GetOrAddComponent<SpriteRenderer>();
                spr.color = color_st;
            }
            if (netbody.plr.locationPingArrow) {
                netbody.plr.locationPingArrow.name = ("fingerpointerarrow_" + name);
                var spr = netbody.plr.locationPingArrow.GetOrAddComponent<SpriteRenderer>();
                spr.color = color_st;
            }
        }


    }





    protected void OnDestroy() {
        if (nametag != null) {
            Destroy(nametag);
        }

        foreach (var item in icons) {
            Destroy(item.Value.icon);
        }
        icons.Clear();

    }
}





public interface ICharacterStatusIcon {

    public void Create(CharStatusVisuals visual, SpriteRenderer spr);

    public bool AppearCondition();
    public bool DisappearCondition();

    public void Update();
}

class KrokoshaMP_CharacterStatusIcon_Healing : ICharacterStatusIcon {
    Body body => visual.body;
    NetBody npc => visual.netbody;

    public bool AppearCondition() {
        return npc.IsPlayer && npc.body.conscious && npc.plr.WoundviewTargetNetBodyId != 0 ;
    } 

    CharStatusVisuals visual;
    SpriteRenderer spr;
    Vector3 localScale = Vector3.one;
    public void Create(CharStatusVisuals visual, SpriteRenderer spr) {
        this.visual = visual;
        this.spr = spr;

        spr.sprite = CoopModAssets.healicon;
        localScale = spr.transform.localScale;
        spr.transform.localScale = localScale * 1.45f;
        
    }

    public bool DisappearCondition() {
        return !AppearCondition();
    }

    public void Update() {
        if (npc.IsPlayer && npc.syncBody != null && npc.plr.WoundviewTargetNetBodyId != npc.netId ) {
            spr.sprite = CoopModAssets.healicon;
            spr.transform.localScale = localScale * 1.4f;
        } else {
            spr.sprite = CoopModAssets.inspectself;
            spr.transform.localScale = localScale * 1.05f;
        }
    }
}


class KrokoshaMP_CharacterStatusIcon_Textchat : ICharacterStatusIcon {
    Body body => visual.body;
    NetBody npc => visual.netbody;
    float counter = 0; 

    public bool AppearCondition() {
        return npc.IsPlayer && npc.plr.IsChatFocused;
    }

    CharStatusVisuals visual;
    SpriteRenderer spr;
    public void Create(CharStatusVisuals visual, SpriteRenderer spr) {
        this.visual = visual;
        this.spr = spr;

        spr.sprite = CoopModAssets.speechbubbleicon0;
        spr.flipX = true;
    }

    public bool DisappearCondition() {
        return !AppearCondition();
    }

    public void Update() {

        counter += Time.unscaledDeltaTime;
        if (counter > 0.75f) {
            spr.sprite = CoopModAssets.speechbubbleicon3;

            if (counter >= 1f) {
                counter = 0;
            }
        } else if (counter > 0.5f) {
            spr.sprite = CoopModAssets.speechbubbleicon2;
        } else if (counter > 0.25f) {
            spr.sprite = CoopModAssets.speechbubbleicon1;
        } else if (counter > 0) {
            spr.sprite = CoopModAssets.speechbubbleicon0;
        }
    }
}




class KrokoshaMP_CharacterStatusIcon_Trading : ICharacterStatusIcon {
    Body body => visual.body;
    NetBody npc => visual.netbody;
    float counter = 0;
    float maxtime = 4;

    Vector3 localScale = Vector3.one;
    public bool AppearCondition() {
        return npc.IsPlayer && npc.plr.IsInTradingMenu;
    }

    CharStatusVisuals visual;
    SpriteRenderer spr;
    public void Create(CharStatusVisuals visual, SpriteRenderer spr) {
        this.visual = visual;
        this.spr = spr;

        localScale = spr.transform.localScale;
        
        Update();

    }

    public bool DisappearCondition() {
        return !AppearCondition();
    }

    public void Update() {

        counter += Time.unscaledDeltaTime;

        if (counter >= maxtime) {
            counter = 0;
        }

        var timedIndex = Mathf.FloorToInt((counter / maxtime) * ((float)(CoopModAssets.trader_icon.Length)));

        spr.sprite = CoopModAssets.trader_icon[timedIndex];
        spr.transform.localScale = localScale * 1.4f;

    }
}






class KrokoshaMP_CharacterStatusIcon_Cmd : ICharacterStatusIcon {
    Body body => visual.body;
    NetBody npc => visual.netbody;

    public bool AppearCondition() {
        return npc.IsPlayer && npc.plr.IsInConsole;
    }

    CharStatusVisuals visual;
    SpriteRenderer spr;
    public void Create(CharStatusVisuals visual, SpriteRenderer spr) {
        this.visual = visual;
        this.spr = spr;

        spr.sprite = CoopModAssets.cmdicon;

    }

    public bool DisappearCondition() {
        return !AppearCondition();
    }

    public void Update() {
        
    }



}

class KrokoshaMP_CharacterStatusIcon_Inventory : ICharacterStatusIcon {
    Body body => visual.body;
    NetBody npc => visual.netbody;

    public bool AppearCondition() {
        return false;
    }

    CharStatusVisuals visual;
    SpriteRenderer spr;
    public void Create(CharStatusVisuals visual, SpriteRenderer spr) {
        this.visual = visual;
        this.spr = spr;

        spr.sprite = WorldMP.whiteSquare;

    }

    public bool DisappearCondition() {
        return !AppearCondition();
    }

    public void Update() {
        
    }



}

class KrokoshaMP_CharacterStatusIcon_AltTab : ICharacterStatusIcon {
    Body body => visual.body;
    NetBody npc => visual.netbody;

    public bool AppearCondition() {
        return npc.IsPlayer && npc.plr.IsAltTab;
    }

    CharStatusVisuals visual;
    SpriteRenderer spr;
    public void Create(CharStatusVisuals visual, SpriteRenderer spr) {
        this.visual = visual;
        this.spr = spr;

        spr.sprite = CoopModAssets.alttab;

    }

    public bool DisappearCondition() {
        return !AppearCondition();
    }

    public void Update() {
        
    }
}

class KrokoshaMP_CharacterStatusIcon_Crafting : ICharacterStatusIcon {
    Body body => visual.body;
    NetBody npc => visual.netbody;

    public bool AppearCondition() {
        return npc.IsPlayer && npc.body.conscious && npc.plr.IsInCraftingMenu;
    }

    CharStatusVisuals visual;
    SpriteRenderer spr;
    public void Create(CharStatusVisuals visual, SpriteRenderer spr) {
        this.visual = visual;
        this.spr = spr;

        spr.sprite = CoopModAssets.crafting;

    }

    public bool DisappearCondition() {
        return !AppearCondition();
    }

    public void Update() {

    }
}

















