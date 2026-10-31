using HarmonyLib;
using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Together; 

internal class CoopModAssets {
    static Dictionary<string, Dictionary<string, UnityEngine.Object>> _cachedAssetBundles = new();
    static Dictionary<string, byte[]> _cachedResources = new();
    static Dictionary<string, Sprite> _cachedSprites = new();


    public readonly static string AssetsPrefix = "Together.assets.";

    public static byte[] ReadResource(string name) {
        if (_cachedResources.TryGetValue(name, out var buf)) {
            return buf;
        }

        string resourceName = AssetsPrefix + name;

        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)) {
            if (stream != null) {
                byte[] buffer = new byte[stream.Length];
                stream.Read(buffer, 0, buffer.Length);

                _cachedResources[name] = buffer;
                return buffer;
            } else {
                Plugin.log.LogError($"Failed to load embedded resource: {resourceName}");
            }
        }
        return null;
    }
    
    public static Dictionary<string, UnityEngine.Object> LoadAllResourcesFromBundle(string name) {
        if (_cachedAssetBundles.TryGetValue(name, out var bndl)) {
            return bndl;
        }
        var curass = new Dictionary<string, UnityEngine.Object>();
        var assetbundle_bytes = ReadResource(name);
        if (assetbundle_bytes != null) {
            var bundle = AssetBundle.LoadFromMemory(assetbundle_bytes);

            foreach (var item in bundle.GetAllAssetNames()) {
                var ass = bundle.LoadAsset(item);
                assets[item] = ass;
                curass[item] = ass;
            }
        }
        _cachedAssetBundles[name] = curass;
        return curass;
    }
    [Obsolete]
    public static Dictionary<string, UnityEngine.Object> LoadAllResroucesFromBundle(string name) => LoadAllResourcesFromBundle(name);

    public static Sprite LoadSprite(string name, float pixelsPerUnit = 100f) {
        if (_cachedSprites.TryGetValue(name, out var spr)) {
            return spr;
        }
        var byt = ReadResource("textures." + name);
        if (byt != null) {
            Texture2D texture = new Texture2D(2, 2);
            texture.name = name;
            ImageConversion.LoadImage(texture, byt);
            texture.filterMode = FilterMode.Point; // nearest neighbor

            var sprite = Util.CreateSprite(texture, pixelsPerUnit);
            sprite.name = name;

            _cachedSprites[name] = sprite;
            return sprite;
        }
        return null;
    }
    
    public static Sprite alttab;

    public static Sprite speechbubbleicon0;
    public static Sprite speechbubbleicon1;
    public static Sprite speechbubbleicon2;
    public static Sprite speechbubbleicon3;

    public static Sprite mpmod_icon;
    public static Sprite mpmod_icon_green;
    public static Sprite star_yellow;

    public static Sprite alert;
    public static Sprite alert2;
    public static Sprite recruit;

    public static Sprite cmdicon;

    public static Sprite arrowicon;
    public static Sprite healicon;
    public static Sprite circlething;
    public static Sprite voicechaticon;

    public static Sprite cpr_button_icon;

    public static Sprite icon_steam;
    public static Sprite icon_crown;

    public static Font gamefont;
    public static Shader coolshader;
    public static Dictionary<string, UnityEngine.Object> assets = new();

    public static Sprite[] trader_icon;

    public static Sprite checkbox;
    public static Sprite checkbox_ticked;

    public static Sprite crafting;
    public static Sprite inspectself;

    public static Sprite aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa;

    public static Sprite @lock;
    public static Sprite happy;
    public static Sprite cross;

    public static void LoadAssets() {


        arrowicon = LoadSprite("arrow.png");
        circlething = LoadSprite("circlething.png");

        voicechaticon = LoadSprite("voicechaticon.png");

        healicon = LoadSprite("healicon.png");
        alttab = LoadSprite("alttab.png");
        cmdicon = LoadSprite("cmdicon.png");

        happy = LoadSprite("happy.png");
        @lock = LoadSprite("lock.png");
        cross  = LoadSprite("shittycross.png");

        icon_crown = LoadSprite("king.png");
        icon_steam = LoadSprite("steam.png");

        speechbubbleicon0 = LoadSprite("speechbubble.png");
        speechbubbleicon1 = LoadSprite("speechbubble1.png");
        speechbubbleicon2 = LoadSprite("speechbubble2.png");
        speechbubbleicon3 = LoadSprite("speechbubble3.png");

        trader_icon = new Sprite[8];
        for (int i = 0; i < trader_icon.Length; i++) {
            trader_icon[i] = LoadSprite($"shop.{(i+1)}.png" );
        }

        mpmod_icon = LoadSprite("mp.png");
        mpmod_icon_green = LoadSprite("mp_green.png");
        star_yellow = LoadSprite("star_yellow.png");

        alert = LoadSprite("excl.png");
        alert2 = LoadSprite("alertthing.png");
        recruit = LoadSprite("recruit.png");

        cpr_button_icon = LoadSprite("cprbutton.png");

        checkbox = LoadSprite("checkbox.png");
        checkbox_ticked = LoadSprite("box_ticked.png");

        crafting = LoadSprite("crafting.png");
        inspectself = LoadSprite("inspectself.png");

        aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa = LoadSprite("krokosha.jpg");

        {
            LoadAllResourcesFromBundle("retrogaming.bundle");
            gamefont = (Font)assets.GetValueSafe("assets/font/retrogaming_200.ttf");
            if (gamefont == null) {
                log.error("!!!!! FAILED TO LOAD GAME FONT !!!!!!!!!");
            }

            LoadAllResourcesFromBundle("cooltextshader.bundle");
            coolshader = (Shader)assets.GetValueSafe("assets/textmesh pro/shaders/animatedgradient.shader");
            if (coolshader == null) {
                log.error("!!!!! animatedgradient FAILED TO LOAD !!!!!!!!!!!!");
            }

        }

    }

#if DEBUG
    public static void Load2() {
        //uiBlockSmall


    }
#endif




}



[HarmonyPatch(typeof(PlushScript), nameof(PlushScript.Start))]
internal static class j {
    static Sprite __ = CoopModAssets.LoadSprite("d.krokosha666.png", 8f);
    internal static bool Prefix(PlushScript __instance) {
        if (!__instance.possibleSprites.Contains(__)) {
            __instance.possibleSprites = __instance.possibleSprites.AddToArray(__).AddToArray(__);
            __instance.possibleSounds = __instance.possibleSounds.AddToArray(__instance.possibleSounds[0]).AddToArray(__instance.possibleSounds[0]);
        }
        return true;
    }
}

