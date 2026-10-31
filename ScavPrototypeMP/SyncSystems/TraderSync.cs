using HarmonyLib;
using Together;
using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Unity.VisualScripting;
using UnityEngine;
using static Together.TrackerTrader;
using static TraderScript;

namespace Together; 



public class TrackerTrader : BaseComponentTracker<TraderScript> {
    public static TraderScript currentTrader;

    public struct TraderStatePacket : INetSerializeByMemcpy {

        public float reputation;
        public float hostility;

        public bool freeDressing;
        public bool didHug;
        public byte freeAmount; // its guranteed to be between 0 and 4

        public float haggleAmount;

        public ushort valueGiven; // but honestly they both cant even reach 255
        public ushort totalValueGiven;

        public TraderStatePacket(TrackerTrader tracker) {
            var trader = tracker.og;
            reputation = trader.reputation;
            hostility = trader.hostility;
            freeDressing = trader.freeDressing;
            didHug = trader.didHug;
            freeAmount = (byte)trader.freeAmount;
            haggleAmount = trader.haggleAmount;
            valueGiven =        (ushort)trader.valueGiven;
            totalValueGiven =   (ushort)trader.totalValueGiven;
        }
        public void Apply(TrackerTrader tracker) {
            var trader = tracker.og;
            trader.reputation = reputation;
            trader.hostility = hostility;
            trader.freeDressing = freeDressing;
            trader.didHug = didHug;
            trader.freeAmount = freeAmount;
            trader.haggleAmount = haggleAmount;
            trader.valueGiven = valueGiven ;
            trader.totalValueGiven = totalValueGiven;
        }

    }

    public BodyGetterOverrider bgo => this.GetOrAddComponent<BodyGetterOverrider>();

    // accessed by get_body in the override TraderScript
    public Body focused_body {
        get {
            return bgo.body;
        }
        set {
            bgo.body = value;
        }
    }


    public bool CanBeRecruited_ForRespawn() {
        return build.GetHealth() > 200 && og.reputation > og.minHugReputation;
    }

    public bool is_registered => (tracker != null);
    public SyncInfo si => tracker?.syncInfo;

    public bool asked_for_inv { get; private set; }
    /// <summary>
    /// aka isregistered
    /// </summary>
    public bool received_their_inv;// { get; private set; }

    protected bool net_didDeathMoodDebuff = false;

    public BuildingEntity build => GetComponent<BuildingEntity>();





    float doublesendRepState = 0;
    public void Server_AnnounceTraderReputationState(in IReadOnlyList<knetid> to_who = null) {
        if (Net.IsClient || (og == null)) {
            return;
        }
        if (!is_registered) {
            NetObjectRegistry.NewGO(gameObject);
            if (!is_registered) {
                log.error($"SERVER: trader snapshot skipped, {build?.id} is not registered");
                return;
            }
        }
        if ((to_who != null) && (to_who.Count == 1)) {
            TraderCommit.PublishTo(og, to_who[0]);
            return;
        }
        TraderCommit.Publish(og);
    }

    public void Server_SendTraderInventory(knetid clientId) {
        if (og == null) {
            return;
        }
        TraderCommit.PublishTo(og, clientId);
    }

    public void Server_SendTraderInventory(List<knetid> clients_to_enlighten) {
        if ((og == null) || (clients_to_enlighten == null) || (clients_to_enlighten.Count == 0)) {
            return;
        }
        if (clients_to_enlighten.Count == 1) {
            TraderCommit.PublishTo(og, clients_to_enlighten[0]);
            return;
        }
        TraderCommit.Publish(og);
    }


    protected override void TrackerAwake() { // called in Awake()

        var owro = this.GetOrAddComponent<OnWillRenderObject_ForceForMPComponent>();
        owro.target = og;
        HookTraderPin();

    }

    void OnEnable() {
        if (Net.IsClient) {
            TraderCommit.Ask(this);
        }
    }

    private static bool pinHooked;

    private static void HookTraderPin() {
        if (pinHooked) {
            return;
        }
        pinHooked = true;
        Camera.onPostRender += PinOpenTrader;
    }

    // stock LateUpdate clears currentTrader, and the next Update closes the menu while it is empty.
    // OnPreRender never ran on the trader. this camera callback is after OnWillRenderObject,
    // so the trader the menu was opened on wins over a neighbour.
    private static void PinOpenTrader(Camera _) {
        if (!Util.IsInTradingMenu() || (currentTrader is null) || (PlayerCamera.main is null)) {
            return;
        }
        PlayerCamera.main.currentTrader = currentTrader;
        TraderPinLog.Sample("pin");
        TrackerTrader tt = currentTrader.GetOrAddComponent<TrackerTrader>();
        tt.focused_body = Util.GetLocalBody();
    }


    void Start() {

        if (Net.IsRunning && Net.IsClient) {
            StartCoroutine(AskWhenReady());
        }


        // fix the bug that makes traders teleport to world center (only if the traders is spawned through non-worldgen means aka command)
        if (og.MoveRange.min == 0 && og.MoveRange.max == 0) {
            og.MoveRange = new RangeF(og.transform.position.x - 5, og.transform.position.x + 5);
        }

        StartCoroutine(ScanForNearestBody());
    }




    // https://docs.unity3d.com/6000.0/Documentation/Manual/execution-order.html
    // i need this execute before OnWillRenderObject, cuz i need to force call it when its outside of hosts view
    void LateUpdate() {
        if (!Net.IsRunning) return;

        if (og.hostile) {
            build.cantHit = false;
        }

        if (Net.IsServer && is_registered) {

            var field_didDeathMoodDebuff = Traverse.Create(og).Field("didDeathMoodDebuff");

            var og_didDeathMoodDebuff = (bool)field_didDeathMoodDebuff.GetValue();

            if (og_didDeathMoodDebuff && !net_didDeathMoodDebuff) {
                net_didDeathMoodDebuff = true;

                // traumatize everyone around except current body there
                YOU_SHOULD_KILL_YOURSELF_NOOOW.DoMoodPenaltyInRadius(
                    focused_body, 
                    5f // half of the original
                    , 10
                );

            }

            if (doublesendRepState > 0) {
                doublesendRepState -= Time.deltaTime;

                if (doublesendRepState < 0) {
                    Server_AnnounceTraderReputationState();
                    doublesendRepState = -900; // Reset it again
                }
            }

        }

    }

    private IEnumerator ScanForNearestBody() {
        while (this.gameObject && this.og) {
            if (Net.IsRunning) {
                bgo.SetNearestBody();
                if (tracker && !tracker.isNearby) {
                    yield return new WaitForSecondsRealtime(2.1021504f);
                }
            }
            yield return new WaitForSecondsRealtime(0.1021504f);
        }
    }

    private IEnumerator AskWhenReady() {
        while (Net.IsClient && Net.IsRunning) {
            SyncInfo si = this.si;
            if ((si != null) && (si.syncId != 0)) {
                TraderCommit.Ask(this);
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.5f);
        }
    }

}



[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.Start))]
static class TraderScript_Start_MultiplayerPatch {
    // stock Start rolls a fresh list and sets reputation back to 100.
    // a reply that arrived first was wiped, and the host copy never came back until a sale.
    static bool Prefix(TraderScript __instance) {
        if (!Net.IsRunning || !Net.IsClient) {
            return true;
        }
        __instance.standAmount = 0f;
        if (__instance.items == null) {
            __instance.items = new List<TraderItem>();
        }
        if (Multiplayer.verbose) {
            FindTheBugInator.Trader("start-skip", FindTheBugInator.TraderState(__instance));
        }
        return false;
    }

    static void Postfix(TraderScript __instance) {
        // only add the tracker 
        __instance.GetOrAddComponent<TrackerTrader>();
        if (Net.IsRunning) {
            __instance.farEnoughToMove = true;
            if (!Net.IsClient) {
                __instance.didMove = false;
                TraderCommit.Publish(__instance);
            }
        }
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.DropInventory) )]
static class TraderScript_DropInventory_MultiplayerPatch {
    static bool Prefix(TraderScript __instance) {
        // inventory drops only on the server,  if im client do nothing and wait for server to spawn it
        if (Net.IsRunning && Net.IsClient) {
            //  __instance.items.Clear(); // i think its better not to, i it will not cause less issues this way
            return false;
        }
        return true;
    }
}



[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.OnUse))]
static class TraderScript_OnUse_MultiplayerPatch {
    static bool Prefix(TraderScript __instance) { // the trader script is so shit idk
        PlayerCamera.main.currentTrader = __instance;
        TrackerTrader.currentTrader = __instance;
        TraderPinLog.Sample("use");
        FindTheBugInator.Trader("use", FindTheBugInator.TraderState(__instance));
        if (Net.IsRunning && Net.IsClient && !Util.IsInTradingMenu()) {
            TraderCommit.Ask(__instance.GetOrAddComponent<TrackerTrader>());
        }
        return true;
    }
}




[HarmonyPatch(typeof(TraderScript), "OnWillRenderObject")]
static class TraderScript_OnWillRender_PinLog {
    private static void Prefix(TraderScript __instance, out (float rep, float host) __state) {
        __state = (__instance == null) ? default : (__instance.reputation, __instance.hostility);
    }
    private static void Postfix(TraderScript __instance, (float rep, float host) __state) {
        // stock ramps hostility on every machine. the host snapshot is the copy that counts.
        if (Net.IsRunning && Net.IsClient && (__instance != null)) {
            __instance.reputation = __state.rep;
            __instance.hostility = __state.host;
        }
        TraderPinLog.AfterWillRender(__instance);
        TraderPinLog.Hold(PlayerCamera.main);
        FindTheBugInator.TraderRamp(__instance);
    }
}

// currentTrader is cleared every LateUpdate, then any nearby trader can write it.
// these lines are which sync id landed, and which null the menu rebuild is about to hit.
static class TraderPinLog {
    static string lastId = "";
    static bool lastMenu;
    static bool haveSample;
    static string lastPainted = "";
    static string lastOpened = "";
    static bool haveMismatch;
    static bool loggedMiss;

    static string SyncText(TraderScript trader) {
        if (trader == null) {
            return "0";
        }
        SyncInfo si;
        if (!NetObjectRegistry.TryGetSyncInfo(trader.gameObject, out si) || (si == null)) {
            TrackerTrader tt = trader.GetComponent<TrackerTrader>();
            if ((tt != null) && (tt.si != null)) {
                si = tt.si;
            }
        }
        if (si == null) {
            return "none";
        }
        return si.syncId.ToString();
    }

    internal static void Hold(PlayerCamera cam) {
        if (!Net.IsRunning || (cam == null) || (cam.tradeMenu == null) || !cam.tradeMenu.activeSelf) {
            return;
        }
        TraderScript opened = TrackerTrader.currentTrader;
        if (opened == null) {
            if (!loggedMiss) {
                loggedMiss = true;
                log.error("[traderpin] hold missed, opened trader is null");
            }
            return;
        }
        cam.currentTrader = opened;
    }

    internal static void Sample(string site) {
        if (!Net.IsRunning || (PlayerCamera.main == null)) {
            return;
        }
        string id = SyncText(PlayerCamera.main.currentTrader);
        bool menu = Util.IsInTradingMenu();
        if (haveSample && (id == lastId) && (menu == lastMenu)) {
            return;
        }
        haveSample = true;
        lastId = id;
        lastMenu = menu;
        if (log.verbose) log.l($"[traderpin] {site} sync={id} menu={(menu ? 1 : 0)}");
    }

    internal static void AfterWillRender(TraderScript writer) {
        if (!Net.IsRunning || (PlayerCamera.main == null) || (PlayerCamera.main.currentTrader != writer) || !Util.IsInTradingMenu()) {
            return;
        }
        string painted = SyncText(writer);
        string opened = SyncText(TrackerTrader.currentTrader);
        if (painted == opened) {
            haveMismatch = false;
            return;
        }
        if (haveMismatch && (painted == lastPainted) && (opened == lastOpened)) {
            return;
        }
        haveMismatch = true;
        lastPainted = painted;
        lastOpened = opened;
        if (log.verbose) log.l($"[traderpin] willrender painted={painted} opened={opened}");
    }

    internal static void LogRefreshFault(PlayerCamera cam) {
        if ((cam == null) || (cam.tradeMenu == null) || !cam.tradeMenu.activeSelf) {
            return;
        }
        TraderScript trader = cam.currentTrader;
        if (trader == null) {
            log.error("[traderui] refresh currentTrader=null menu=open");
            return;
        }
        string id = SyncText(trader);
        if (trader.items == null) {
            log.error($"[traderui] refresh sync={id} items=null");
            return;
        }
        if (trader.collapsedCategories == null) {
            log.error($"[traderui] refresh sync={id} collapsedCategories=null items={trader.items.Count}");
            return;
        }
        if (cam.traderInventory == null) {
            log.error($"[traderui] refresh sync={id} traderInventory=null items={trader.items.Count}");
            return;
        }
        for (int i = 0; i < trader.items.Count; i++) {
            object boxed = trader.items[i];
            if (boxed == null) {
                log.error($"[traderui] refresh sync={id} items[{i}]=null");
                return;
            }
            TraderItem item = trader.items[i];
            if (string.IsNullOrEmpty(item.id)) {
                log.error($"[traderui] refresh sync={id} items[{i}] id empty");
                return;
            }
            GameObject prefab = Resources.Load(item.id) as GameObject;
            if (prefab == null) {
                log.error($"[traderui] refresh sync={id} items[{i}] id={item.id} prefab=null");
                return;
            }
            SpriteRenderer rend = prefab.GetComponent<SpriteRenderer>();
            if ((rend == null) || (rend.sprite == null)) {
                log.error($"[traderui] refresh sync={id} items[{i}] id={item.id} sprite=null");
                return;
            }
            if (rend.sprite.texture == null) {
                log.error($"[traderui] refresh sync={id} items[{i}] id={item.id} texture=null");
                return;
            }
            if (Item.GetItem(item.id) == null) {
                log.error($"[traderui] refresh sync={id} items[{i}] id={item.id} iteminfo=null");
                return;
            }
        }
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.Swing))]
static class TraderScript_Swing_MultiplayerPatch {
    private static bool Prefix(TraderScript __instance) {
        FindTheBugInator.TraderSwing(__instance);
        // a client swing only hits the local body. real damage is the host event.
        if (Net.IsRunning && Net.IsClient) {
            return false;
        }
        TrackerTrader tt = __instance.GetOrAddComponent<TrackerTrader>();

        tt.bgo.SetNearestBody();
        return true;
    }
    static void Postfix(TraderScript __instance) { // send an update packet cuz in swing it moves  desiredPos  which is quite important in combat
        if (!Net.IsRunning) return;
        if (Net.IsServer) {
            //if (NetObjectRegistry.TryGetSyncInfo(__instance, out var si)) NetObjectRegistry.Server_QueueSync(si);

            var tt = __instance.GetOrAddComponent<TrackerTrader>();
            if (tt.focused_body != null && tt.focused_body.TryGetNetBody(out var body)) {
                body.OnDamageEvent(new BodyDamageEvent(__instance.build, body, 10));
            }
            TraderCommit.Publish(__instance);
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.AskToMove))]
public static class TraderScript_AskToMove_MultiplayerPatch {
    static bool Prefix(TraderScript __instance) {
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Move);
    }

    static void Postfix(TraderScript __instance) {
        if (!Net.IsRunning) return;
        
        if (Net.IsClient) {

        } else {
            var tt = __instance.GetOrAddComponent<TrackerTrader>();

            //NetObjectRegistry.Server_QueueSync(tt.si);
        }
    }



    [ServerReceiver((byte)NetmsgId.SERVER_TraderAskToMove)]
    static void Server_TraderAskToMove(knetid terroristid, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"TraderAskToMove actually works  {DebugHelp.GetPlayerFullDebugString(terroristid)} ");
#endif

        reader.Get(out knetid targettrader);

        if (TraderSync.Server_TraderInteractionCheck(terroristid, targettrader, "AskToMove", out ScavPlayer plr, out SyncInfo si, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Move, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targettrader, out si) && (si.trader != null)) {
            TraderCommit.PublishTo(si.trader, terroristid);
        }
    }


    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}










[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.Threaten))]
public static class TraderScript_Threaten_MultiplayerPatch {
    public static bool Prefix(TraderScript __instance) {
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Threaten);
    }

    public static void Postfix(TraderScript __instance) {
        

        if (Net.IsRunning) {
            if (Net.IsClient) {

            } else {
                var tt = __instance.GetOrAddComponent<TrackerTrader>();

                tt.Server_AnnounceTraderReputationState();
            }
        }
    }



    [ServerReceiver((byte)NetmsgId.SERVER_TraderThreaten)]
    static void Server_TraderThreaten(knetid terroristid, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"TraderThreaten actually works  {DebugHelp.GetPlayerFullDebugString(terroristid)} ");
#endif

        reader.Get(out knetid targettrader);

        if (TraderSync.Server_TraderInteractionCheck(terroristid, targettrader, "Threaten", out ScavPlayer plr, out SyncInfo si, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Threaten, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targettrader, out si) && (si.trader != null)) {
            TraderCommit.PublishTo(si.trader, terroristid);
        }
    }


    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}





[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.TryHug))]
public static class TraderScript_TryHug_MultiplayerPatch {
    public static bool Prefix(TraderScript __instance, ref float __state) {
        __state = __instance.reputation;
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Hug);
    }

    public static void Postfix(TraderScript __instance, ref float __state) {

        if (Net.IsRunning) {
            if (Net.IsClient) {

            } else {
                var tt = __instance.GetOrAddComponent<TrackerTrader>();

                if (__instance.hostility != 100f && __state != __instance.reputation) {
                    if (__instance.reputation > __state) {

                    } else {

                        if (!Util.IsBodyLocal(tt.focused_body) && tt.focused_body.TryGetNetPlayer(out var plr)) { 
                            // im host, remind them of ragdoll
                            plr.Server_RemindPlayersCurrentState(true);
                        }
                    }
                }

                tt.Server_AnnounceTraderReputationState();
            }
        }
    }



    [ServerReceiver((byte)NetmsgId.SERVER_TraderTryHug)]
    static void Server_TraderTryHug(knetid terroristid, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"TraderTryHug actually works  {DebugHelp.GetPlayerFullDebugString(terroristid)} ");
#endif

        reader.Get(out knetid targettrader);

        if (TraderSync.Server_TraderInteractionCheck(terroristid, targettrader, "Hug", out ScavPlayer plr, out SyncInfo si, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Hug, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targettrader, out si) && (si.trader != null)) {
            TraderCommit.PublishTo(si.trader, terroristid);
        }
    }


    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}




[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.PromptResponse))]
static class TraderScript_PromptResponse_MultiplayerPatch {
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(Talker), nameof(Talker.PromptTraderResponse))]
static class Talker_PromptTraderResponse_MultiplayerPatch {
    static bool Prefix(Talker __instance, string textType) {
        if (!Net.IsRunning) return true;

        if (__instance.body != null && __instance.body.alive) {
            var phys_in_radius = Physics2D.OverlapCircleAll(__instance.body.transform.position, 20);
            var only_traders = phys_in_radius.Where(x => x.GetComponent<TraderScript>() != null).Select(x => x.gameObject);

            foreach (var t in only_traders) {
                var track = t.GetOrAddComponent<TrackerTrader>();
                track.focused_body = __instance.body;

                track.og.PromptResponse(textType);
            }
        }
        return false;
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.TryHaggle))]
public static class TraderScript_TryHaggle_MultiplayerPatch {
    
    static bool Prefix(TraderScript __instance, ref float __state) {
        __state = __instance.reputation;
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Haggle);
    }

    static void Postfix(TraderScript __instance, ref float __state) {
        float num = __state - __instance.reputation;

        if (Net.IsRunning) {
            var tt = __instance.GetOrAddComponent<TrackerTrader>();
            tt.Server_AnnounceTraderReputationState();
        }

    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderTryHaggle)]
    static void Server_TraderTryHaggle(knetid terroristid, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG 
        log.l($"TraderTryHaggle actually works  {DebugHelp.GetPlayerFullDebugString(terroristid)} ");
#endif

        reader.Get(out knetid targettrader);

        if (TraderSync.Server_TraderInteractionCheck(terroristid, targettrader, "TryHaggle", out ScavPlayer plr, out SyncInfo si, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Haggle, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targettrader, out si) && (si.trader != null)) {
            TraderCommit.PublishTo(si.trader, terroristid);
        }
    }


    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}










[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.GiveItem))]
public static class TraderScript_GiveItem_MultiplayerPatch {
    public static bool Prefix(TraderScript __instance, Item item) {
        if (!Net.IsRunning || TraderCommit.Running) {
            return true;
        }
        TrackerTrader tt = __instance.GetOrAddComponent<TrackerTrader>();
        if ((tt.si is null) || (tt.si.syncId == 0) || (item == null) || !ItemSync.TryGetSyncInfo(item, out SyncInfo itemsi)) {
            return false;
        }
        if (Net.IsServer) {
            if ((PlayerCamera.main == null) || (PlayerCamera.main.body == null)) {
                return false;
            }
            TraderCommit.SubmitSell(PlayerCamera.main.body, tt.si, itemsi.syncId);
            return false;
        }
        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_TraderSellItem, tt.si.syncId, itemsi.syncId, true);
        return false;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderSellItem)]
    private static void Server_TraderSell(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
#if DEBUG
        log.l($"TraderSellItem actually works  {DebugHelp.GetPlayerFullDebugString(clientId)} ");
#endif

        reader.Get(out knetid targettrader);
        reader.Get(out knetid solditem);

        

        if (TraderSync.Server_TraderInteractionCheck(clientId, targettrader, "SellItem", out ScavPlayer plr, out SyncInfo trader_si, out _)) {
            TraderCommit.SubmitSell(plr.body, trader_si, solditem);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targettrader, out trader_si) && (trader_si.trader != null)) {
            TraderCommit.PublishTo(trader_si.trader, clientId);
        }
    }


    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}




















[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.body), MethodType.Getter)]
static class TraderScript_get_body_MultiplayerPatch {

#if !RELEASE
    static bool _getter_working_announced = false;
    static void Prefix() {
        if (!_getter_working_announced) {
            _getter_working_announced = true;
            Plugin.log.LogInfo($"MethodType.Getter  is working: {new StackTrace()}");
        }
    }
#endif 

    static void Postfix(TraderScript __instance, ref Body __result) {
        if (!Net.IsRunning) return;
        Body actor = TraderCommit.Actor;
        if (actor != null) {
            __result = actor;
            return;
        }
        var tt = __instance.GetOrAddComponent<TrackerTrader>();
        var bgo = tt.bgo;
        if (bgo.body == null) {
            bgo.SetNearestBody();
            if (bgo.body == null) {
                bgo.body = PlayerCamera.main.body;
            }
        }
        __result = bgo.body;
    }
}


// this is called only from PlayerCamera (by clicking on a button)
[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.TryPurchase))]
internal static class TraderScript_TryPurchase_MultiplayerPatch {

    public static Item TryPurchase_rewritten(TraderScript trader, TraderItem titem, Body buyer) {
        var is_local = Util.IsBodyLocal(buyer);

        Item did_buy = null;
        var tracker = trader.GetComponent<TrackerTrader>();
        var build = trader.GetComponent<BuildingEntity>();
        if (build.GetHealth() < 200f) {
            return did_buy;
        }
        if (trader.valueGiven >= trader.ItemPrice(titem)) {
            trader.valueGiven -= trader.ItemPrice(titem);
            if (trader.ItemPrice(titem) > 0) {
                if (titem.preference == TraderItemPreference.WantsTrade) {
                    trader.reputation += 7f;
                } else if (titem.preference == TraderItemPreference.Indifferent) {
                    trader.reputation += 4f;
                }
            }
            if (tracker.og.freeAmount > 0) {
                tracker.og.freeAmount--;
            } else {
                trader.talker.Talk(Locale.GetCharacter("traderbuy", trader.character));
                buyer.happiness += 0.75f;
                buyer.skills.AddExp(2, 1f);
            }
            if (titem.info.HasTag("dressing")) {
                tracker.og.freeDressing = false;
            }
            trader.items.Remove(titem);
            GameObject gameObject = Utils.Create(titem.id, trader.transform.position, 0f);
            gameObject.AddComponent<BoughtItem>();
            if (gameObject.TryGetComponent<AmmoScript>(out var component) && component.itemType == AmmoScript.AmmoItemType.Magazine) {
                component.rounds = UnityEngine.Random.Range((int)((float)component.maxRounds * 0.5f), component.maxRounds);
            }
            buyer.AutoPickUpItem(gameObject.GetComponent<Item>());
            if (is_local) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Click);
            } else {
                TraderCommit.RelaySound(trader, buyer, "click", trader.transform.position);
            }
            did_buy = gameObject.GetComponent<Item>();
        } else {
            if (trader.totalValueGiven != MAX_VALUE_GIVEN) {
                trader.talker.Talk(Locale.GetCharacter("traderbuyfail", trader.character));
            } else {
                trader.talker.Talk(Locale.GetCharacter("traderbuyfailmaxvalue", trader.character));
            }
            if (is_local) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
            } else {
                TraderCommit.RelaySound(trader, buyer, "warning", trader.transform.position);
            }
            trader.reputation -= 2f;
        }
        if ((PlayerCamera.main != null) && (PlayerCamera.main.currentTrader == trader) && Util.IsInTradingMenu()) {
            PlayerCamera.main.RefreshTraderInventories();
            PlayerCamera.main.UpdateTradeTexts();
        }
        return did_buy;
    }



    static bool Prefix(TraderScript __instance, TraderItem item) {
        if (!Net.IsRunning || TraderCommit.Running) {
            return true;
        }
        if ((PlayerCamera.main == null) || (PlayerCamera.main.currentTrader != __instance) || (item == null)) {
            return true;
        }
        TrackerTrader tt = __instance.GetOrAddComponent<TrackerTrader>();
        if ((tt.si == null) || (tt.si.syncId == 0)) {
            return false;
        }
        byte index = (byte)__instance.items.IndexOf(item);
        if (Net.IsServer) {
            TraderCommit.SubmitBuy(PlayerCamera.main.body, tt.si, index, item.id, (ushort)item.value);
            return false;
        }
        var writer = Net.CreateWriter(NetmsgId.SERVER_Trader_TryPurchase);
        writer.Put(tt.si.syncId);
        writer.Put((ushort)item.value);
        writer.Put(index);
        writer.Put(item.id, true);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        return false;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Trader_TryPurchase)]
    static void Server_Trader_TryPurchase(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid trader_syncid);
        reader.Get(out ushort aleged_value);
        reader.Get(out byte aleged_index);
        reader.Get(out string itemId, true);

        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body buyer) &&
            NetObjectRegistry.TryGetSyncInfoLive(trader_syncid, out SyncInfo isi) && isi.IsTrader() &&
            KM.DistanceCheck(buyer, isi, 20)
        ) {
            TraderCommit.SubmitBuy(buyer, isi, aleged_index, itemId, aleged_value);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(trader_syncid, out isi) && (isi.trader != null)) {
            TraderCommit.PublishTo(isi.trader, clientId);
        }
    }
}


[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.MeetPlayer))]
public static class TraderScript_MeetPlayer_MultiplayerPatch {

    // its the thing that calculates reputation from first impression
    public static (float,float,bool) MeetPlayer_CalculateFirstImpression(Body body) {
        float reputation = 0f;
        float hostility = 0f;
        bool freeDressing = false;

        //OG: TraderScript.MeetPlayer  V5t2 THE ENTIRE FUNCTION I COPIED IT HERE

        reputation = UnityEngine.Random.Range(75f, 135f);
        reputation += body.skills.INTFrom10 * 4f;
        if (body.talker.impairedSpeech) {
            reputation -= 20f;
        }
        if (body.disfigured) {
            reputation -= 5f;
        }
        if (body.dirtyness > 50f) {
            reputation -= body.dirtyness * 0.25f;
        }
        if (body.HoldingItem(body.handSlot) && body.GetItem(body.handSlot).Stats.HasTag("gun")) {
            hostility += 50f;
            reputation -= 20f;
        }
        if ((bool)body.mindWipe) {
            reputation *= 0.7f;
        }
        reputation -= (100f - body.brainHealth) * 0.5f;
        reputation += body.happiness * 0.5f;
        if (body.hearingLoss > 50f) {
            reputation -= 20f;
        }
        if (body.totalBleedSpeed > 0.001f) {
            freeDressing = true; 
        }
        return (reputation, hostility, freeDressing);
    }


    public static bool Prefix(TraderScript __instance, ref TraderStatePacket __state) {
        if (Net.IsRunning) {
            var tt = __instance.GetOrAddComponent<TrackerTrader>();

            __state = new TraderStatePacket(tt);

        }
        return true;
    }
    public static void Postfix(TraderScript __instance, ref TraderStatePacket __state) {
        if (Net.IsRunning) {
            var tt = __instance.GetOrAddComponent<TrackerTrader>();

            __instance.eyes.sprite = __instance.eyeSprites[1];
            Traverse.Create(__instance).Field("sitTime").SetValue(Time.time);
            __instance.startedConvo = true;

            if (Net.IsServer) {

                var plrs = ScavPlayer.GetPlayerBodiesInRadius(__instance.transform.position, 16f);

                float reputation = 0f;
                float hostility = 0f;
                bool freeDressing = false;

                foreach (var pb in plrs) {

                    var res = MeetPlayer_CalculateFirstImpression(pb.body);

                    reputation = Mathf.Max(res.Item1, reputation);
                    hostility = Mathf.Min(res.Item2, hostility);
                    if (!freeDressing) {
                        freeDressing = res.Item3;
                    }

                }

                __instance.reputation = reputation;
                __instance.hostility = hostility;
                tt.og.freeDressing = freeDressing;
            
                tt.Server_AnnounceTraderReputationState();
            } else {
                __state.Apply(tt);// dont change it on client, wait for release
            }
            FindTheBugInator.Trader("meet", "savedRep=" + __state.reputation.ToString("0.#") + " savedHostl=" + __state.hostility.ToString("0.#") + " " + FindTheBugInator.TraderState(__instance));

        }
    }
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}








public class TraderSync : CasualtiesTogetherSingleton {


    // if it returns true it also set the focused body to the guy
    public static bool Server_TraderInteractionCheck(knetid plrid, knetid traderid, in string interaction_name_for_log, out ScavPlayer plr, out SyncInfo trader_si, out TrackerTrader trader_tracker  ) {
       
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(plrid, out plr, out var terrorist) && terrorist.conscious &&
            NetObjectRegistry.TryGetSyncInfoLive(traderid, out trader_si) && trader_si.IsTrader() &&
            //Util.AccurateRaycastInteractionCheck(terrorist, si.go.transform.position, false) &&
            KM.DistanceCheck(terrorist, trader_si, 20)
        ) {
            trader_tracker = trader_si.trader.GetOrAddComponent<TrackerTrader>();

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(trader_tracker.transform.position, $"S: trader {interaction_name_for_log} - {plr.playerName} -> {trader_si}");

            return true;
        }
        trader_si = null;
        trader_tracker = null;


        log.serverdeny($"trader {trader_si} {interaction_name_for_log} for {plr}");

        return false;
    }



























    internal static void SendTraderState(knetid clientId) {
        if (Net.IsClient || (clientId == default)) {
            return;
        }
        Body body;
        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out _, out body) || (body == null)) {
            FindTheBugInator.Trader("join", "client=" + clientId.id + " body=null");
            return;
        }
        Vector2 pos = body.transform.position;
        int sent = 0;
        int skipped = 0;
        foreach (SyncInfo si in NetObjectRegistry.SyncRegistry.Values) {
            if ((si == null) || (si.go == null) || si.IsDestroyed || !si.IsTrader() || (si.trader == null)) {
                continue;
            }
            if (!TraderCommit.InRange(si.trader.transform.position, pos)) {
                skipped++;
                continue;
            }
            sent++;
            TraderCommit.PublishTo(si.trader, clientId);
        }
        FindTheBugInator.Trader("join", "client=" + clientId.id + " pos=" + pos.x.ToString("0.#") + "," + pos.y.ToString("0.#") + " sent=" + sent + " skipped=" + skipped);
    }

}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.DoRest))]
internal static class TraderScript_DoRest_MultiplayerPatch {
    private static bool Prefix(TraderScript __instance) {
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Rest);
    }
}



















