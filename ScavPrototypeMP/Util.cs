using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using Together;
using Unity.VisualScripting;
using UnityEngine;
using static Unity.VisualScripting.Member;

namespace CasualtiesTogetherUtils;


public static class PlayerPrefsExtended {
    public static bool GetBool(string key, bool defaultValue = false) {
        return PlayerPrefs.GetInt(key, defaultValue.ToInt32()) != 0;
    }
    public static void SetBool(string key, bool value) {
        PlayerPrefs.SetInt(key, value.ToInt32());
    }
}


#region UTIL_PATCHES 

[HarmonyPatch(typeof(Settings), nameof(Settings.DefaultSettings))]
internal static class Settings_DefaultSettings_Patch {
    public static Dictionary<string, KeyCode> custom_binds = new();
    static void Postfix(ref List<Setting> __result) {
        foreach (var keybind in custom_binds)
            if (!__result.Any(x => x.name == keybind.Key)) {
                __result.Add(new SettingKeybind {
                    name = keybind.Key,
                    value = keybind.Value,
                    apply = delegate {
                    },
                    category = Setting.SettingCategory.Input
                });
            }
    }
}
[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.Start))]
internal static class SettingsMenu_Start_Patch {
    public static bool forceOpenCustomTab;
    static bool Prefix(SettingsMenu __instance) {
        if (SettingsMenu_Start_Patch.forceOpenCustomTab) {
            SettingsMenu_Start_Patch.forceOpenCustomTab = false;
            return false;
        }
        return true;
    }
}
[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.SelectTab), new Type[] {typeof(Setting.SettingCategory)})]
internal static class SettingsMenu_SelectTab_Patch {
    internal static int currentSelectedTab;
    static bool Prefix(SettingsMenu __instance, Setting.SettingCategory category) {
        currentSelectedTab = (int)category;
        return true;
    }
}



#endregion


public static class Util_UnityExtensions {

    public static Rect GetScreenRect(this RectTransform rt, Camera camera = null) {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);

        var min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        var max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);

        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetParent(this Transform t, Component p, bool worldPositionStays = true) => t.SetParent(p.transform, worldPositionStays);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetParent(this Transform t, GameObject p, bool worldPositionStays = true) => t.SetParent(p.transform, worldPositionStays);

}

public static class Util_MiscSystemExtensions {

    public static void AddIfNotPresent<T>(this List<T> list, T value) {
        if (!list.Contains(value))
            list.Add(value);
    }

    public static T PickRandom<T>(this IEnumerable<T> source) {
        return source.ElementAt(UnityEngine.Random.Range(0, source.Count()));
    }

    public static int CountOccurences(this string text, string substring) {
        int c = 0;
        int i = 0;
        while ((i = text.IndexOf(substring,i)) != -1) {
            c++;
            i += substring.Length;
        }
        return c;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToInt32(this bool b) {
        return b ? 1 : 0;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte(this bool b) {
        return b ? (byte)1 : (byte)0;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ToBool(this byte b) {
        return b != (byte)0;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ToBool(this int i) {
        return i != 0;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Clamp(this Vector2 v2, in Vector2 a, in Vector2 b) {
        return new Vector2(Math.Clamp(v2.x, a.x, b.x), Math.Clamp(v2.y, a.y, b.y));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Clamp(this Vector2 v2, in float a, in float b) {
        return new Vector2(Math.Clamp(v2.x,a,b), Math.Clamp(v2.y, a, b));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Abs(this Vector2 v2) {
        return new Vector2(Math.Abs(v2.x), Math.Abs(v2.y));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Max(this Vector2 v2, in Vector2 other) {
        return new Vector2(Math.Max(v2.x,other.x), Math.Max(v2.y, other.y));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Min(this Vector2 v2, in Vector2 other) {
        return new Vector2(Math.Min(v2.x, other.x), Math.Min(v2.y, other.y));
    }


    public static (IEnumerable<T> True, IEnumerable<T> False) Partition<T>(this IEnumerable<T> source, Func<T, bool> predicate) {
        var trueList = new List<T>();
        var falseList = new List<T>();

        foreach (var item in source) {
            if (predicate(item))
                trueList.Add(item);
            else
                falseList.Add(item);
        }

        return (trueList, falseList);
    }

    public static byte[] ToBytes(this BitArray bits) {
        int numBytes = (bits.Length + 7) / 8;
        byte[] bytes = new byte[numBytes];
        bits.CopyTo(bytes, 0);
        return bytes;
    }

    public static string ToStringInvariant(this object obj) {
        string aaa = null; 
        var prev = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            aaa = obj.ToString();
        } finally {
            CultureInfo.CurrentCulture = prev;
        }
        return aaa;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInRange(this RangeF r, in float number) {
        return number >= r.min && number <= r.max;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Center(this RangeF r) {
        return Mathf.LerpUnclamped(r.min,r.max,0.5f);
    }
    public static RangeF Widen(this RangeF r, in float number) {
        var r1 = new RangeF(r.min, r.max);
        r1.min -= number;
        r1.max += number;
        return r1;
    }
    public static Rect ShrinkBorder(this Rect r, in float number) {
        var reeeee = new Rect(r);
        var n = Vector2.one * number;
        reeeee.min += n;
        reeeee.max -= n;
        return reeeee;
    }

    public static int StableHash(this string s) { // ai generated
        if (s == null)
            return 0;
        
        unchecked {
            const int fnvPrime = 16777619;
            int hash = (int)2166136261;

            byte[] bytes = Encoding.UTF8.GetBytes(s);

            foreach (byte b in bytes) {
                hash ^= b;
                hash *= fnvPrime;
            }
            return hash;
        }
    }
    public static byte[] GetUTF8Bytes(this string s) {
        return Encoding.UTF8.GetBytes(s);
    }
    public static string ToStringFromUTF8Bytes(this byte[] b) {
        return Encoding.UTF8.GetString(b);
    }
    public static byte[] ToByteArray(this string s) {
        var arr = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) {
            arr[i] = (byte)s[i];
        }
        return arr;
    }
    public static string ToStringFromCharBytes(this byte[] b) {
        var arr = new StringBuilder(b.Length);
        foreach (var c in b) {
            arr.Append((char)c);
        }
        return arr.ToString() ;
    }


    // ai generated
    public static void ShuffleRandom<T>(this IList<T> array) {
        for (int i = array.Count - 1; i > 0; i--) {
            int randomIndex = UnityEngine.Random.Range(0, i + 1);

            // Swap
            T temp = array[i];
            array[i] = array[randomIndex];
            array[randomIndex] = temp;
        }
    }

    public static bool TryTrimStart(this string source, string value, out string trimmed) {
        if (source.StartsWith(value)) {
            trimmed = source.Substring(value.Length);
            return true;
        }
        trimmed = source;
        return false;
    }
    public static bool TryTrimStart(this string source, string value, out string trimmed, bool ignoreCase) {
        if (source.StartsWith(value, ignoreCase, CultureInfo.InvariantCulture)) {
            trimmed = source.Substring(value.Length);
            return true;
        }
        trimmed = source;
        return false;
    }
    public static bool TryTrimEnd(this string source, string value, out string trimmed, bool ignoreCase) {
        if (source.EndsWith(value, ignoreCase, CultureInfo.InvariantCulture)) {
            trimmed = source.Substring(0, source.Length - value.Length);
            return true;
        }
        trimmed = source;
        return false;
    }

    public static Texture2D Clone(this Texture2D s) {
        var t = new Texture2D(s.width, s.height, s.format, s.mipmapCount > 1);
        t.SetPixels(s.GetPixels());
        t.Apply();
        return t;
    }
    public static Sprite Clone(this Sprite spr) {
        var t = spr.texture.Clone();
        return  Sprite.Create(t,spr.rect,spr.pivot,spr.pixelsPerUnit);
    }
    public static void TextureBW(this Texture2D t) {
        var p = t.GetPixels();
        for (int i = 0; i < p.Length; i++) {
            //var c = p[i];
            //var avg = Mathf.Max(c.r, c.g ,c.b);
            //p[i] = new Color(avg, avg, avg, c.a);
            
            p[i] = p[i].ColorBW();
        }
        t.SetPixels(p);
    }
    public static Color ColorBW(this Color t) {
        var avg = Mathf.Max(t.r, t.g, t.b);
        return new Color(avg, avg, avg, t.a);
    }

}

public static class Util_MiscCUExtensions {
    
    public static Dictionary<int, List<string>> GetArgAutofill(this Command com) {
        if (com.argAutofill == null) {
            com.argAutofill = new();
        }
        return com.argAutofill;
    }
    public static List<string> GetArgAutofillList(this Command com, int arg) {
        if (com.argAutofill == null) {
            com.argAutofill = new();
        }
        if (!com.argAutofill.TryGetValue(arg, out var result)) {
            result = new();
            com.argAutofill[arg] = result;
        }
        return result;
    }
    public static Vector2 GetPosition(this DamagingCrate crate) {
        return crate.transform.position;
    }

    public static T GetOrAddComponent<T>(this CrystalEffect ce) where T : Component {
        var uo = ce.crystal;
        return uo.GetComponent<T>() ?? uo.AddComponent<T>();
    }

}


public static class Util_ItemExtensions {

    public static bool IsEdible(this Item item) {
        return item.Stats.category == "food" || (item.id == "foliage" || item.id == "dryfoliage");
    }

    public static bool IsHandcrank(this Item item) {
        return item.id == "handcrank";
    }

    public static bool IsAED(this Item item) {
        return item.id == "aed";
    }
    public static bool IsManualDefibrillator(this Item item) {
        return item.id == "manualdefibrillator";
    }


    public static Sprite GetSprite(this Item item) {
        return item.GetComponent<SpriteRenderer>().sprite;
    }

    public static bool IsItemOrBuilding(this GameObject go) {
        return go.GetComponent<Item>() != null || go.GetComponent<BuildingEntity>() != null;
    }

    //affect.wasWater
    public static bool IsInWater(this Item item) {
        return item.affect.wasWater;
    }
}

public static class Util_ContainerExtensions {

    public static List<Item> GetAllItems(this Container container) {
        var list = new List<Item>(container.transform.childCount);    
        foreach (Transform item in container.transform) 
            if (item.TryGetComponent<Item>(out var component)) 
                list.Add(component);
        return list;
    }

}


public static class Util_LimbExtensions {
    public static byte GetIndex(this Limb limb) {
        // this is actually Array.IndexOf but less safe but faster 
        for (byte i = 0; i < (byte)limb.body.limbs.Length; i++) {
            if (limb == limb.body.limbs[i]) {
                return i;
            }
        }
        return 0;
    }

    public static Vector2 GetPosition(this Limb limb) {
        return limb.transform.position;
    }
    public static bool IsBodyLocal(this Limb limb) {
        return limb.body.IsBodyLocal();
    }

}


public static class Util_BodyExtensions {

    public static bool IsBodyLocal(this Vomiter vomiter) {
        return PlayerCamera.main.body == vomiter.body;
    }
    public static bool IsBodyLocal(this Body body) {
        return PlayerCamera.main.body == body;
    }
    public static Vector2 GetPosition(this Body body) {
        return body.transform.position;
    }
    public static Vector2 GetVelocity(this Body body) {
        if (body.standing) {
            return body.rb.velocity;
        } else {
            return body.GetUpperTorso().rb.velocity;
        }
    }

    public static bool IsAirBreathable(this Body body) {
        return !body.inWater || body.hasScubaGear;
    }

    public static bool IsMindwiped(this Body body) {
        return body.mindWipe != null && body.mindWipe.active;
    }

    public static bool IsRibsBroken(this Body body) {
        //OG: MoodleManager  V510 
        return body.limbs[1].broken;
    }
    public static bool IsRagdolling(this Body body) => !body.standing;


    public static bool IsAliveAndUnconsciousAndNotSleeping(this Body body) {
        if (body.alive) {
            return !body.conscious && !body.sleeping;
        }
        return false;
    }


    public static Limb GetPrimaryHand(this Body body) => body.limbs[(body.handSlot == 0) ? 5 : 8];
    public static Limb GetSecondaryHand(this Body body) => body.limbs[(body.handSlot == 0) ? 8 : 5];

    public static FacialExpression GetFace(this Body body) => body.limbs[(int)Body.LimbNum.Head].GetComponent<FacialExpression>();
    public static PantSound GetPantSound(this Body body) => body.GetComponent<PantSound>();
    public static Limb GetHead(this Body body) => body.limbs[(int)Body.LimbNum.Head];
    public static Limb GetUpperTorso(this Body body) => body.limbs[(int)Body.LimbNum.UpTorso];
    public static Limb GetLowerTorso(this Body body) => body.limbs[(int)Body.LimbNum.DownTorso];



    public static Limb GetClosestLimb(this Body body, Vector2 pos, out float dist_sqr) {
        dist_sqr = 999999f;
        Limb result = body.limbs[0]; //head
        foreach (Limb limb in body.limbs) {
            if (!limb.dismembered && KM.DistanceCheck_PreSqr(pos, limb.transform.position, dist_sqr, out var newdist)) {
                result = limb;
                dist_sqr = newdist;
            }
        }
        return result;
    }


    public static void ResetMind(this Body body) {
        body.Body_ResetSkills();
        if (body.TryGetNetBody(out var nb) && nb.IsPlayer) {
            nb.ResetMind();
        }
        body.corpsesSeen = 0;
        body.desensitizedMult = 1;

    }

    public static void ResetHealth(this Body body, bool unmindwipe = true) {
        
        // regrow missing limbs (ts kinda broken still)
        body.RegrowAllLimbs();


        foreach (var limb in body.limbs) {
            limb.muscleHealth = 100f;
            limb.skinHealth = 100f;
            limb.boneHealTimer = 0f;
            limb.dislocationTimer = 0f;
            limb.infectionAmount = 0f;
            limb.bleedAmount = 0f;
            limb.pain = 0f;
            limb.shrapnel = 0;
            limb.furBloodAmount = 0f;
            limb.infected = false;
            
            if (limb.TryGetComponent(out SplintLimb splint)) {
                splint.condition = 0;
                splint.TakeOff();
            }
            if (limb.TryGetComponent(out TourniquetScript tour)) {
                tour.condition = 0;
                tour.TakeOff();
            }
        }

        body.brainHealth = 100f;

        body.bloodVolume = 100f;
        body.bloodOxygen = 100f;
        body.bloodPressure = 120f;
        body.heartRate = 70f;
        body.bloodVesselSize = 1f;
        body.bloodViscosity = 0f;
        body.respiratoryRate = 100f;
        body.strokeAmount = 0f;
        body.hasPulmonaryEmbolism = false;
        body.fibrillationProgress = 0f;

        body.hunger = 100f;
        body.thirst = 100f;
        body.septicShock = 0f;
        body.temperature = 37f;
        body.sicknessAmount = 0f;
        body.consciousness = 100f;
        body.stamina = 100f;
        body.energy = 100f;
        body.happiness = 0f;
        body.weightOffset = 0f;
        body.radiationSickness = 0f;
        body.disfigured = false;
        body.eyeGone = false;
        body.bothEyesGone = false;
        body.internalBleeding = 0f;
        body.hemothorax = 0f;
        body.traumaAmount = 0f;
        body.dirtyness = 0f;
        body.wetness = 0f;
        body.badSleepAmount = 0f;
        body.hearingLoss = 0f;
        body.antidepressantHappiness = 0f;
        body.antibioticImmunityTime = 0f;
        body.brainGrowSickness = 0f;
        body.triedRollingLastStand = false;
        body.succesfullyRolledLastStand = false;
        body.reversedControls = false;
        body.lastStandTime = -1000f;
        body.adrenaline = 0f;
        body.curAdrenaline = 0f;
        body.opiateHappiness = 0f;
        body.venomCurrent = 0f;
        body.venomTotal = 0f;
        body.clawHealth = 100f;
        body.bloodPressureChangeFromMedicine = 0f;
        body.caffeinated = 0f;
        body.clawRegrowTime = 0f;
        body.painShock = 0f;

        if (body.TryGetComponent(out Painkillers component)) 
            UnityEngine.Object.Destroy(component);
        if (body.TryGetComponent(out SleepingPills component2)) 
            UnityEngine.Object.Destroy(component2);
        if (body.TryGetComponent(out Antidepressants component3))
            UnityEngine.Object.Destroy(component3);

        if (body.TryGetNetBody(out var nb)) {
            // To ensure attempt to get the real coutils instance
            if (nb.IsPlayer && nb.plr.CoUtilsInstance != null) {
                var coutil = nb.plr.CoUtilsInstance;
                coutil.CancelAll();
            }
            nb.OnDamageEvent(new BodyDamageEvent(BodyDamageEvent.DamageEventType.Revive, nb));
        }

        var OGcoutil = CoUtils_instance_MultiplayerPatch.cur_override_instance;
        CoUtils_instance_MultiplayerPatch.cur_override_instance = body.GetCoUtilsInstance();

        CoUtils.instance.CancelAll();

        CoUtils_instance_MultiplayerPatch.cur_override_instance = OGcoutil;

        if (unmindwipe) {
            if (body.TryGetComponent(out MindwipeScript component222222))
                UnityEngine.Object.Destroy(component222222); // the vignette is removed by other patch
                                                             //component222222.active = false; // or do i do this???
        }

    }






    //OG: Body.ForceAutoPickUpItem V7.0.1
    public static void ForceAutoPickUpItem(this Body body, Item item) {
        //Only removed the noautopickup check

        if (!item.Stats.wearable) {
            int? num = body.FirstEmptySlot();
            if (!num.HasValue) {
                foreach (Item surfaceInventoryItem in body.GetSurfaceInventoryItems()) {
                    if ((bool)surfaceInventoryItem.container && surfaceInventoryItem.container.CanHoldItem(item)) {
                        surfaceInventoryItem.container.LoadItem(item);
                        break;
                    }
                }

                return;
            }

            body.PickUpItem(item, num.Value, force: true);
        } else {
            Item wearableBySlotID = body.GetWearableBySlotID(item.Stats.wearSlotId);
            if ((bool)wearableBySlotID) {
                body.DropItem(wearableBySlotID);
            }

            body.WearWearable(item);
            PlayerCamera.main.UpdateWearables();
        }
    }




    public static string DumpBodyVars(this Body body) {
        var sb = new StringBuilder();
        sb.AppendLine("BODY VARS DUMP:");

        try {
            {
                var bodyfields = typeof(Body).GetFields(BindingFlags.Instance | BindingFlags.Public).Where((FieldInfo f) => f.FieldType.IsPrimitive).ToArray();
                foreach (var field in bodyfields) {
                    sb.AppendLine($"\t{field.Name}: {field.GetValue(body)}");
                }
            }
            {
                if (body.TryGetComponent(out Painkillers painkill)) {
                    var painkillfields = typeof(Painkillers).GetFields(BindingFlags.Instance | BindingFlags.Public).Where((FieldInfo f) => f.FieldType.IsPrimitive).ToArray();

                    sb.AppendLine("Painkillers:");
                    foreach (var field in painkillfields) {
                        sb.AppendLine($"\t{field.Name}: {field.GetValue(painkill)}");
                    }
                }
            }
            {
                sb.AppendLine("Limbs:");
                var limbfields = typeof(Limb).GetFields(BindingFlags.Instance | BindingFlags.Public).Where((FieldInfo f) => f.FieldType.IsPrimitive).ToArray();
                foreach (var limb in body.limbs) {
                    sb.AppendLine($"\t{limb.name} - {limb.fullName} - {limb.shortName}:");
                    if (limb.dismembered) {
                        sb.AppendLine($"\t\tAMPUTATED");
                    } else {
                        foreach (var field in limbfields) {
                            sb.AppendLine($"\t\t{field.Name}: {field.GetValue(limb)}");
                        }
                    }
                }
            }
            {
                sb.AppendLine("CoUtils:");
                var inst = body.GetNetBody()?.plr?.CoUtilsInstance;
                if (inst == null) {
                    sb.AppendLine("NULL");
                } else {
                    if (inst.activeOps != null) {
                        if (inst.activeOps.Count == 0) {
                            sb.AppendLine("ZERO ACTIVE");
                        } else {
                            foreach (var item in inst.activeOps) {
                                sb.AppendLine(item.ToString());
                            }
                        }
                    } else {
                        sb.AppendLine("ERROR");
                    }
                }
            }
        } catch (Exception ex) {
            sb.AppendLine(ex.ToString());
        }

        return sb.ToString();
    }



}

public static class Util {

    public static WorldGeneration world => WorldGeneration.world;
    public static ushort[,] worldBlocks => WorldGeneration.world.worldBlocks;
    public static byte[,] fluid => FluidManager.main.fluid;

    #region SOUND_STUFF

    public static AudioSource PlayWorldSoundOnScreenIfInRange(in string snd, in Vector2 pos, in float volume = 1, in float pitch = 1, in float range = 64) {
        if (KM.DistanceCheck(pos, Camera.main.transform.position, range)) {
            var s = Sound.Play(snd, pos, false, true, null, volume, pitch);
            if (s != null) {
                s.maxDistance = range;
                s.minDistance = range * 0.4f;
                s.rolloffMode = AudioRolloffMode.Linear;
                return s;
            }
        }
        return null;
    }

    


    #endregion

    #region LAMBDA_STUFF

    public static ulong GetFileHash(string filePath) {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offsetBasis;
        byte[] buffer = new byte[8192];

        using (FileStream stream = File.OpenRead(filePath)) {
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0) {
                for (int i = 0; i < bytesRead; i++) {
                    hash ^= buffer[i];
                    hash *= prime;
                }
            }
        }

        return hash;
    }


    public static void CallLambdaWhenWorldGenerates(Delegate func) {
        StartCoroutine(_CoroutineCallLambdaWhenWorldGenerated(func));
    }

    static IEnumerator _CoroutineCallLambdaWhenWorldGenerated(Delegate func) {
        while (!Util.IsWorldGenerated()) { yield return null; }

        func.DynamicInvoke();
    }

    public static void CallLambdaWhenWorldLoads(Delegate func) {
        StartCoroutine(_CoroutineCallLambdaWhen(() => {
            return Util.IsInWorld() && worldBlocks != null;
        }, func));
    }
    public static void CallLambdaWhenWorldFirstGenerates(Delegate func) {
        StartCoroutine(_CoroutineCallLambdaWhen(Util.IsWorldGenerated, func));
    }

    //  Util.CallLambdaWhen(() => { return true; }, () => { });
    public static void CallLambdaWhen(Func<bool> condition, Delegate func) {
        StartCoroutine(_CoroutineCallLambdaWhen(condition, func));
    }

    public static void CallLambdaWhen(Func<bool> condition, Delegate func, float check_repeat_time = 0.1f) {
        StartCoroutine(_CoroutineCallLambdaWhen(condition, func, check_repeat_time));
    }

    internal static IEnumerator _CoroutineCallLambdaWhen(Func<bool> condition, Delegate func) {
        while (!condition.Invoke()) { yield return null; }
        func.DynamicInvoke();
    }
    private static IEnumerator _CoroutineCallLambdaWhen(Func<bool> condition, Delegate func, float check_repeat_time) {
        while (!condition.Invoke()) { yield return new WaitForSecondsRealtime(check_repeat_time); }
        func.DynamicInvoke();
    }

    public static void DelayCallLambda(float delay_sec, Delegate func) {
        StartCoroutine(_DelayCallLambdaCoroutine(delay_sec, func));
    }
    
    internal static IEnumerator _DelayCallLambdaCoroutine(float delay_sec, Delegate func) {
        yield return new WaitForSecondsRealtime(delay_sec);

        func.DynamicInvoke();
    }





    private class CoroutineRunner : MonoBehaviour {
        static CoroutineRunner cr = null;
        public static CoroutineRunner get() {
            if (cr == null) {
                var go = new GameObject("KrokoshaCoroutineRunner");
                DontDestroyOnLoad(go);
                cr = go.AddComponent<CoroutineRunner>();
                go.hideFlags = HideFlags.HideAndDontSave;
            }
            return cr;
        }
        public static void run(IEnumerator f) => get().StartCoroutine(f);
    }


    public static void StartCoroutine(Func<IEnumerator> f) => CoroutineRunner.run(f());
    public static void StartCoroutine(IEnumerator f) => CoroutineRunner.run(f);



    public static Task StartCoroutineAsync(IEnumerator coroutine) {
        var tcs = new TaskCompletionSource<object>();
        CoroutineRunner.get().StartCoroutine(RunCoroutineForAsync(coroutine,tcs));
        return tcs.Task;
    }
    private static IEnumerator RunCoroutineForAsync(IEnumerator coroutine, TaskCompletionSource<object> tcs) {
        yield return coroutine;

        if (!tcs.Task.IsCompleted) {
            tcs.SetResult(null);
        }
    }

    #endregion

    #region GAME_SCENE_STATE

    public static bool IsWorldGenerated() {
        if (WorldGeneration.world && !WorldGeneration.world.generatingWorld) {
            return true;
        }
        return false;
    }
    public static bool IsWorldInstantiated() {
        if (WorldGeneration.world && !WorldGeneration.world.instantiatingWorld) {
            return true;
        }
        return false;
    }
    public static bool IsGeneratingWorld() {
        if (WorldGeneration.world) {
            return WorldGeneration.world.generatingWorld;
        }
        return false;
    }
    public static bool IsTutorialWorld() {
        if (WorldGeneration.world != null && WorldGeneration.world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial) {
            return true;
        }
        return false;
    }
    public static bool IsInWorld() {
        if (WorldGeneration.world != null) {
            return true;
        }
        return false;
    }
    public static bool IsInMainMenu() {
        if (WorldGeneration.world == null) {// or (PreRunScript.instance != null) 
            return true;
        }
        return false;
    }
    public static bool IsInPauseMenuOrMainMenu() {
        if (IsInWorld()) {
            if (PauseHandler.main != null) {
                if (PauseHandler.main.isPaused) {
                    return true;
                }
                return false;
            }
        }
        return true;
    }
    public static bool IsInPauseMenu() {
        if (PauseHandler.main != null) {
            if (PauseHandler.main.isPaused) {
                return true;
            }
            return false;
        }
        return false;
    }
    public static bool IsInGameAndPauseMenu() {
        if (IsInWorld()) {
            if (PauseHandler.main != null) {
                if (PauseHandler.main.isPaused) {
                    return true;
                }
                return false;
            }
        }
        return false;
    }
    public static void OpenBrightnessPanel(bool open_or_nah) {
        if (PauseHandler.main != null && PauseHandler.main.isPaused != open_or_nah) {
            PauseHandler.TogglePause();
        }
    }
    public static void OpenSettingsPanel(bool open_or_nah) {
        OpenSettingsPanel(open_or_nah, 0);
    }
    public static void OpenSettingsPanel(bool open_or_nah, Setting.SettingCategory category) {
        OpenSettingsPanel(open_or_nah, (int)category);
    }
    public static void OpenSettingsPanel(bool open_or_nah, int category) {
        if (open_or_nah) {
            if (SettingsMenu.instance == null) {
                SettingsMenu.OpenMenu(Util.IsInMainMenu() ? PreRunScript.instance.mainCanvas.transform : PauseHandler.main.transform);
            }
            SettingsMenu_Start_Patch.forceOpenCustomTab = true;
            if (SettingsMenu.instance.spawnedSettings == null) {
                SettingsMenu.instance.spawnedSettings = new List<GameObject>();
            }
            SettingsMenu.instance.SelectTab(category);
        } else {
            if (SettingsMenu.instance != null) {
                SettingsMenu.instance.Close();
            }
        }
    }
    public static bool IsSettingsOpen() {
        return SettingsMenu.instance != null;
    }
    public static Setting.SettingCategory GetSettingsPanelOpenedCategory() {
        return (Setting.SettingCategory)SettingsMenu_SelectTab_Patch.currentSelectedTab;
    }
    #endregion

    #region WORLD_STUFF

    public static bool IsInWorldBoundaries(in Vector2 pos) {
        return Math.Abs(pos.x) < (float)world.width * 0.5f && Math.Abs(pos.y) < (float)world.height * 0.5f;
    }

    public static float TilesToMeters(float tiles_distance) {
        //OG: WorldGeneration.PlayerLayerDepthMeters V5t4
        return tiles_distance * 0.3f;
    }

    public static float MetersToTiles(float meters_distance) {
        //OG: WorldGeneration.PlayerLayerDepthMeters V5t4
        return meters_distance / 0.3f;
    }


    /// <summary>
    /// Fills rectangular area with blocks of set id
    /// Input is in Block coordinates
    /// </summary>
    public static void BlockFill(int x, int y, int endx, int endy, ushort block_id) {
        if (world != null) {
            if (x > endx) {
                var g = x;
                x = endx;
                endx = x;
            }
            if (y > endy) {
                var g = y;
                y = endy;
                endy = y;
            }

            x = Math.Max(x, 0);
            y = Math.Max(y, 0);

            BlockFillUnsafe(
                x,
                y,
                Math.Min(endx, (int)WorldGeneration.world.width),
                Math.Min(endy, (int)WorldGeneration.world.height),
                block_id
            );
        }

    }


    /// <summary>
    /// Fills rectangular area with blocks of set id
    /// Input is in Block coordinates
    /// No safety checks for better performance
    /// </summary>
    public static void BlockFillUnsafe(int startX, int startY, int endX, int endY, ushort block_id) {

        var tiles = worldBlocks;

        for (int x = startX; x <= endX; x++) {
            for (int y = startY; y <= endY; y++) {
                tiles[x, y] = block_id;
            }
        }
        if (!world.generatingWorld) {

            for (int x = startX / WorldGeneration.CHUNKSIZE; x <= endX / WorldGeneration.CHUNKSIZE; x++) {
                for (int y = startY / WorldGeneration.CHUNKSIZE; y <= endY / WorldGeneration.CHUNKSIZE; y++) {
                    WorldGeneration.world.UpdateChunk(new Vector2Int(x, y));
                }
            }

        }
    }






    /// <summary>
    /// Adds a custom layer modifier to the pool of randomly selectable layer modifiers.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public static void RegisterLayerModifier<T>() where T : LayerModifier {
        var inst = Activator.CreateInstance(typeof(T), LayerModifier.availableModifiers.Length) as LayerModifier;

        LayerModifier.availableModifiers = LayerModifier.availableModifiers.AddToArray(inst);
    }


    #endregion

    #region KEYBINDS

    /// <summary>
    /// CALL THIS IN PLUGIN WAKE UP TO MAKE SURE THEY LOAD WITHOUT REQUIRING IT TO MANUALLY RESET
    /// </summary>
    public static void RegisterKeybind(string key, KeyCode code) {
        Settings_DefaultSettings_Patch.custom_binds[key] = code;
    }

    #endregion

    #region LOCAL_BODY_STUFF
    public static Vector2 GetLocalBodyPosition() {
        if (TryGetLocalBody(out var b)) {
            return b.transform.position;
        }
        return Camera.main.transform.position;
    }
    public static bool IsLocalBodyAlive() {
        if (TryGetLocalBody(out var body)) {
            return body.alive;
        }
        return false;
    }
    public static Body GetLocalBody() {
        return PlayerCamera.main.body;
    }
    public static Body GetLocalBodyNullable() {
        if (PlayerCamera.main == null) {
            return null;
        }
        return PlayerCamera.main.body;
    }
    public static bool TryGetLocalBody(out Body body) {
        if (PlayerCamera.main == null) {
            body = null;
            return false;
        }
        body = PlayerCamera.main.body;
        return body != null;
    }
    public static bool IsBodyLocal( Body body) {
        return PlayerCamera.main?.body == body;
    }
    public static bool IsBodyLocal( Limb limb) {
        return limb.body.IsBodyLocal();
    }
    #endregion

    public static bool IsUnchipped() {
        if (Util.IsInWorld()) {
            return WorldGeneration.unchipped;
        } else {
            return (bool)WorldMP.runSettings.GetValueOrDefault("unchipped", false);
        }
    }
    public static void SetUnchipped(bool unchipped) {
        if (IsInWorld()) {
            WorldGeneration.world.SetUnchipped(unchipped);
        } else {
            if (!Multiplayer.rules.UnchippedIsIndividual) {
                WorldMP.runSettings["unchipped"] = unchipped;
            }
        }
    }

    #region UI_UTILS




    public static void DoAlert(in string text, in bool important = false) => PlayerCamera.main.DoAlert(text, important);

    //OG: PlayerCamera.PlayUISound V6.0-pre8   but this one is static so it can be used in main menu
    static Dictionary<PlayerCamera.UISoundType, string> _PlayUISound_NAMES = new Dictionary<PlayerCamera.UISoundType, string> {
        { PlayerCamera.UISoundType.Click, "click"},
        { PlayerCamera.UISoundType.MiniClick, "miniClick"},
        { PlayerCamera.UISoundType.Close, "close"},
        { PlayerCamera.UISoundType.HealthPanelOn, "woundon"},
        { PlayerCamera.UISoundType.InventoryOpen, "menuOpen"},
        { PlayerCamera.UISoundType.InventoryClose, "menuClose"},
        { PlayerCamera.UISoundType.Deny, "warning"},
        { PlayerCamera.UISoundType.Time, "time"},
    };
    public static void PlayUISound(PlayerCamera.UISoundType t, float pitch = 1f, float volume = 1f) {
        var name = _PlayUISound_NAMES.GetValueSafe(t) ?? "warning";

        Sound_Play_MultiplayerPatch.force = true;
        Sound.Play(name, Vector2.zero, twoDimensional: true, pitchShift: false, null, volume, pitch, noReverb: true, ignoreMixer: true);
    }



    public static bool WoundViewAlertIsShown() {
        return IsInWoundView() && WoundView.view.criticalPanel.activeInHierarchy;
    }
    public static void WoundViewShowAlertText(in string msg) {
        if (IsInWoundView()) {
            WoundView.view.criticalPanel.SetActive(value: true);
            var txt = WoundView.view.criticalPanel.GetComponentInChildren<TextMeshProUGUI>(true);
            if ((bool)txt) {
                //OG: WoundView.UpdateView  V520dev1
                txt.enabled = Mathf.Sin(Time.unscaledTime * 20f) > 0f;
                txt.text = msg;
            }
        }
    }

    public static void ToggleWoundView() {
        ToggleWoundView(!IsInWoundView());
    }
    public static void ToggleWoundView(bool target, bool sound = true) {
        if (PlayerCamera.main != null) {
            if (target != IsInWoundView()) {
                PlayerCamera.main.ToggleWoundView(sound);
            }
        }
    }
    public static bool IsInWoundView() {
        return PlayerCamera.main != null && PlayerCamera.main.woundView.activeSelf;
    }
    public static bool IsInConsole() {
        return (bool)ConsoleScript.instance && ConsoleScript.instance.active;
    }
    public static bool IsInCraftingMenu() {
        return PlayerCamera.main != null && PlayerCamera.main.craftingPanel.activeSelf;
    }
    public static bool IsInTradingMenu() {
        return PlayerCamera.main != null && PlayerCamera.main.tradeMenu.activeSelf;
    }

    public static void CreatePickupLine(Vector2 from, Vector2 to) {
        //OG:  Body.DoPickupCheck V5t4
        GameObject obj = Utils.Create(("Special/PickupLine"), Vector2.zero, 0);
        obj.GetComponent<LineRenderer>().SetPosition(0, from);
        obj.GetComponent<LineRenderer>().SetPosition(1, to);
        UnityEngine.Object.Destroy(obj, 0.5f);
    }


    #endregion


    // used for Body, mostly server side

    public static bool QuickGroundLinecastCheck(in Vector2 from, in Vector2 to) {
        return Physics2D.Linecast(from, to, LayerMask.GetMask("Ground"));
    }


    public static bool QuickRaycastInteractionCheck(Vector2 from, Vector2 to, bool do_effect) {
        bool flag = Physics2D.Linecast(from, to, LayerMask.GetMask("Ground"));
        if (flag && ((bool)Physics2D.OverlapPoint(from, LayerMask.GetMask("Ground")) || (bool)Physics2D.OverlapPoint(to, LayerMask.GetMask("Ground")))) {
            flag = false;
        }
        if (flag && do_effect) {
            CreatePickupLine(from, to);
        }
        return !flag;
    }



    public static bool AccurateRaycastInteractionCheck(Body who, Vector2 target, bool do_effect) {

        bool flag = Physics2D.Linecast(who.GetLowerTorso().transform.position, target, LayerMask.GetMask("Ground"));

        if (flag) {
            flag = Physics2D.Linecast(who.GetHead().transform.position, target, LayerMask.GetMask("Ground"));
        }
        if (flag && do_effect) {
            CreatePickupLine(who.transform.position, target);
        }
        return !flag;
    }

    public static bool AccurateRaycastInteractionCheckObstruction(Body who, Body target, bool do_effect) {

        if (who == target) {
            return true;
        }

        var g = LayerMask.GetMask("Ground");

        bool flag = Physics2D.Linecast(who.GetHead().transform.position, target.GetHead().transform.position, g);

        if (flag) {
            flag = Physics2D.Linecast(who.GetLowerTorso().transform.position, target.GetLowerTorso().transform.position, g);
            if (flag) {
                flag = Physics2D.Linecast(who.GetHead().transform.position, target.GetLowerTorso().transform.position, g);
                if (flag)
                    flag = Physics2D.Linecast(who.GetLowerTorso().transform.position, target.GetHead().transform.position, g);
            }
        }
        if (flag && do_effect) {
            CreatePickupLine(who.transform.position, target.transform.position);
        }
        return !flag;
    }


    public static bool DoFullInteractionCheck(Body who, Body target, bool do_effect, float distance = 9f, bool check_obstruction = true) {
        if (who == target) {
            return true;
        }
        if (!KM.DistanceCheck(who.transform.position, target.transform.position, distance)) {
            return false;
        }
        if (check_obstruction && !AccurateRaycastInteractionCheckObstruction(who, target, do_effect)) {
            return false;
        }
        return true;
    }





    public static bool TryGetBodyFromObject(Component component, out Body body) {
        if (component != null && Utils.GetBody(component.gameObject, out body)) {
            return true;
        }
        body = null;
        return false;
    }
    public static bool TryGetBodyFromObject(GameObject gameObject, out Body body) {
        if (gameObject != null && Utils.GetBody(gameObject,out body)) {
            return true;
        }
        body = null;
        return false;
    }



    public static Body GetBodyOnPos(Vector2 pos) {
        var a = Physics2D.OverlapPointAll(pos);
        foreach (var item in a) {
            if (item.TryGetComponent<Limb>(out var c)) {
                return c.body;
            } else if (item.TryGetComponent<Body>(out var b)) {
                return b;
            }
        }
        return null;
    }






    public static Vector2 GetResolution() {
        return new Vector2(Screen.width, Screen.height);
    }
    public static Vector2 GetCursorWorldPos() {
        return Camera.main.ScreenToWorldPoint(UnityEngine.Input.mousePosition);
    }
    public static Vector2 GetCursorPos() {
        return (UnityEngine.Input.mousePosition);
    }





    public static Vector2 PlaceBody_FindSpawnLocation(float x = 0) {
        
        //OG: Body.PlaceBody V7.0-pre1
        bool flag = false;
        for (int i = 0; i < WorldGeneration.world.height; i++) {
            var p = new Vector2(x, WorldGeneration.world.halfHeight - i);
            if (!Physics2D.OverlapBox(p, Body_Awake_MultiplayerPatch.origColSize, 0f, LayerMask.GetMask("Ground"))) {
                flag = true;
            } else if (flag) {
                return p;
            }
        }
        return new Vector2(x, WorldGeneration.world.halfHeight * 0.75f);
    }












    public static int CalculateMaxRepeats<T>(List<T> list) {
        var occurences = list.GroupBy(x => x).Select(g => new { Value = g.Key, Count = g.Count() });
        return occurences.Max(g => g.Count);
    }
    public static List<T> LimitMaxRepeats<T>(List<T> list, int maxrepeats) {
        var occurences = list.GroupBy(x => x).SelectMany(g => g.Take(maxrepeats)).ToList();
        return occurences;
    }



    public static bool WorldPositionIsOnScreen(Vector2 pos) => WorldPositionIsOnScreen(pos, Vector2.zero);
    public static bool WorldPositionIsOnScreen(Vector2 pos, Vector2 bounds) {
        var cam = Camera.main;

        float hh = cam.orthographicSize;
        float hw = hh * cam.aspect;

        var campos = (Vector2)cam.transform.position;

        float minX = campos.x - hw + bounds.x;
        float maxX = campos.x + hw - bounds.x;
        float minY = campos.y - hh + bounds.y;
        float maxY = campos.y + hh - bounds.y;

        return pos.x.IsInRange(minX, maxX) && pos.y.IsInRange(minX, maxX);
    }
    public static Vector2 ClampWorldPositionToCameraView(Vector2 pos) {
        return ClampWorldPositionToCameraView(pos, Vector2.zero);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="pos"></param>
    /// <param name="bounds">is basically half size of the obj</param>
    /// <returns></returns>
    public static Vector2 ClampWorldPositionToCameraView(Vector2 pos, Vector2 bounds) {

        var cam = Camera.main;

        float hh = cam.orthographicSize;
        float hw = hh * cam.aspect;

        var campos = (Vector2)cam.transform.position;

        float minX = campos.x - hw + bounds.x;
        float maxX = campos.x + hw - bounds.x;
        float minY = campos.y - hh + bounds.y;
        float maxY = campos.y + hh - bounds.y;

        return new Vector2(
                Mathf.Clamp(pos.x, minX, maxX),
                Mathf.Clamp(pos.y, minY, maxY)
        );
    }





    public static void ForceSwitchDir(this Body body, bool isRight) {
        if (isRight != body.isRight) {
            ForceSwitchDir(body);
        }
    }
    public static void ForceSwitchDir(this Body body) {
        var wasstanding = body.standing;
        body.standing = true;
        body.SwitchDir(); // it works only when body.standing is true,  and it doesnt seem to break anything anyway 
        body.standing = wasstanding;
    }

    public static void ResetEntropy(this Body body) {
        body.moveDir = Vector2.zero;
        body.SetVelocity(Vector2.zero);
        body.ForceStand();
    }

    public static void ForceStand(this Body body) {
        if (!body.standing) {
            body.shock = 0f;
            body.Stand(true);
        }
    }

    
    public static bool IsDeadOrCriticallyDying(this Body body) {
        return !body.alive || body.isCriticallyDying;
    }


    public static bool IsAnyLimbMissing(this Body body) {
        return body.limbs.Any(x => x.dismembered);
    }

    // I don't know how this works, or if it works
    public static void RegrowAllLimbs(this Body body) {
        if (body.IsAnyLimbMissing()) {
            
            var ogbodypos = body.transform.position;

            var wasstanding = body.standing;
            
            if (!body.isRight) {
                body.ForceSwitchDir();
            }
            body.Ragdoll();


            var a = body.GetComponentsInChildren<ClientMain.SavedHingeJointState>(true);
            
            foreach (var item in a) {
            
                item.transform.localPosition = item.a;
                item.transform.localRotation = item.b;
            
                item.connectedBody.transform.localPosition = item.aa;
            
                var euler_with_reference = item.bb.eulerAngles;
                euler_with_reference.z = item.transform.localRotation.eulerAngles.z - item.referenceAngle;
            
                item.connectedBody.transform.localRotation = Quaternion.Euler(euler_with_reference);
            
            }
            for (int i = 0; i < body.limbs.Length; i++) {
                var limb = body.limbs[i];
                limb.gameObject.SetActive(false);
                limb.dismembered = false;
            }


            foreach (Limb limb in body.limbs) {
                limb.gameObject.SetActive(true);
                limb.dismembered = false;
            }

            foreach (var item in a) {
                item.hinge.connectedBody = item.connectedBody;
            }


            foreach (Limb limb in body.limbs) {

                // because this shit gets reset for some reason 
                var ca = limb.GetComponent<Collider2D>();
                foreach (var other_npc in NetBody.all_instances) {
                    var other_chara = other_npc.transform.parent;
                    var b = other_chara.GetComponentsInChildren<Collider2D>(true);
                    foreach (var cb in b) {
                        Physics2D.IgnoreCollision(ca, cb, true);
                    }
                }
            }

            body.shock = 0;
            body.transform.position = ogbodypos;
        }
    }

    public static void Body_DropAllItems(this Body body) {
        for (int i = 0; i < body.slots.Length; i++) 
            if ((bool)body.GetItem(i)) 
                body.DropItem(i);
        foreach (var item in body.GetAllWearables()) 
            body.DropWearable(item);
    }
    public static void Body_ResetSkills(this Body body) {
        body.skills.Setup(body.charType);
    }



    public static void SetSetting(string name, object value) {
        var setting = Settings.settings.Find((Setting s) => s.name == name);
        setting.SetValue(value);
    }






    public static byte GetLimbIndex(Limb limb) => limb.GetIndex();
    

    public static Limb GetClosestLimb(this Body body, Vector2 pos, out float dist_sqr) {
        dist_sqr = 999999f;
        Limb result = body.limbs[0]; //head
        foreach (Limb limb in body.limbs) {
            if (!limb.dismembered && KM.DistanceCheck_PreSqr(pos, limb.transform.position, dist_sqr, out var newdist) ) {
                result = limb;
                dist_sqr = newdist;
            }
        }
        return result;
    }

    public static bool TryGetLocalDiscordUser(out DiscordRPC.User user) {
        if (RPCManager.instance != null && RPCManager.instance.client != null && RPCManager.instance.client.CurrentUser != null) {
            user = RPCManager.instance.client.CurrentUser;
            return true;
        }
        user = null;
        return false;
    }










    public static bool TryGetTalkerOnObject(SyncInfo si, out Talker talker, out string name) {
        if (si != null) {
            return TryGetTalkerOnObject(si.go, out talker, out name);
        }
        talker = null;
        name = "";
        return false;
    }
    public static bool TryGetTalkerOnObject(GameObject go, out Talker talker) => TryGetTalkerOnObject(go, out talker, out var _);
    public static bool TryGetTalkerOnObject(GameObject go, out Talker talker, out string name) {

        name = "";
        talker = null;

        if (go != null) {
            if (go.TryGetComponent(out Body body)) {
                name = body.name;
                if (body.TryGetNetBody(out var npc)) { // get player username
                    name = npc.bodyName;
                }
                talker = body.talker;
            } else if (go.TryGetComponent(out talker)) {
                name = talker.name;
                //talker = talker;
            } else if (go.TryGetComponent(out Limb limb)) {
                body = limb.body;
                name = body.name;
                if (body.TryGetNetBody(out var npc)) {// get player username
                    name = npc.bodyName;
                }
                talker = body.talker;
            } else if (go.TryGetComponent(out TraderScript trader)) {
                name = trader.name;
                talker = trader.talker;
            }
        }

        return talker != null;
    }


    /// <summary>
    /// DISTANCE IS SQUARED BTW !!!!!!
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="all_instances"></param>
    /// <param name="pos"></param>
    /// <returns></returns>
    public static (T, float) GetNearestComponent<T>(in List<T> all_instances, Vector2 pos) where T : MonoBehaviour  {
        float dist = float.PositiveInfinity;
        T nearest = null;
        foreach (var c in all_instances  ) {
            var cdist = KM.DistanceSqr(c.transform.position, pos);
            if (cdist < dist) {
                dist = cdist;
                nearest = c;
            }
        }
        return (nearest, (dist));
    }
    


    public static Sprite CreateSprite(Texture2D texture, float pixelsPerUnit = 100f) {
        return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
    }




    public static bool IsFinite(this float value) {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
    public static bool IsFinite(this Vector2 value) {
        return IsFinite(value.x) && IsFinite(value.y);
    }

    // Get field, with backup names
    public static FieldInfo GetField(Type type, params string[] fieldNames) {
        foreach (var f in fieldNames) {
            var field = AccessTools.Field(type, f);
            if (field != null)
                return field;
        }
        return null;
    }
    public static FieldInfo GetField<T>(params string[] fieldNames) {
        return GetField(typeof(T), fieldNames);
    }


    public static MethodInfo GetMethod(Type typ, params string[] methodNames) {
        foreach (var m in methodNames) {
            var method = typ.GetMethod(m, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (method != null) 
                return method;
        }
        return null;
    }

    public static MethodInfo GetMethod<T>(params string[] methodNames) {
        return GetMethod(typeof(T), methodNames);
    }


    private static bool? _checkedIfIsDemo = null;
    public static bool GameIsDemo() { // Stolen from CUCoreLib
        if (_checkedIfIsDemo.HasValue) return _checkedIfIsDemo.Value;

        string[] FullGameOnlyTypeNames = {
            "GuardScript",
            "GuardSpawner",
            "EndLabScript",
        };

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var typeName in FullGameOnlyTypeNames) {
            foreach (var assembly in assemblies) {
                Type resolved;
                try {
                    resolved = assembly.GetType(typeName, false);
                } catch {
                    resolved = null;
                }

                if (resolved == null) continue;

                matched.Add(typeName);
                break;
            }

            if (matched.Count >= 2) break;
        }

        _checkedIfIsDemo = matched.Count < 2;
        return _checkedIfIsDemo.Value;
    }
    public static bool GameIsFull() => !GameIsDemo();

    #region COMPRESSION





    public static byte[] Compress(byte[] data) {
        if (KrokV5Optimization.FixUtilCompress.Try(data, out byte[] pooled))
            return pooled;
        using var outputstream = new MemoryStream();
        using (var gzipstream = new GZipStream(outputstream, System.IO.Compression.CompressionLevel.Optimal)) {
            gzipstream.Write(data, 0, data.Length);
        }
        return outputstream.ToArray();
    }

    public static byte[] Decompress(byte[] compressed_data) {
        if (KrokV5Optimization.FixUtilDecompress.Try(compressed_data, out byte[] pooled))
            return pooled;
        using var inputstream = new MemoryStream(compressed_data);
        using var gzipstream = new GZipStream(inputstream, CompressionMode.Decompress);
        using var outputstream = new MemoryStream();
        gzipstream.CopyTo(outputstream);
        return outputstream.ToArray();
    }


    public static byte[] CompressDeflate(byte[] data) {
        if (KrokV5Optimization.FixUtilCompressDeflate.Try(data, out byte[] pooled))
            return pooled;
        using var outputstream = new MemoryStream();
        using (var gzipstream = new DeflateStream(outputstream, System.IO.Compression.CompressionLevel.Optimal)) {
            gzipstream.Write(data, 0, data.Length);
        }
        return outputstream.ToArray();
    }

    public static byte[] DecompressDeflate(byte[] compressed_data) {
        if (KrokV5Optimization.FixUtilDecompressDeflate.Try(compressed_data, out byte[] pooled))
            return pooled;
        using var inputstream = new MemoryStream(compressed_data);
        using var gzipstream = new DeflateStream(inputstream, CompressionMode.Decompress);
        using var outputstream = new MemoryStream();
        gzipstream.CopyTo(outputstream);
        return outputstream.ToArray();
    }



    #endregion


}






















