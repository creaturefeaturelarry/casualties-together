#if FULLGAME
using System.Collections;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace Together;

// merc cutscene stays on that peer, then spectate. escape and lab dialogue stay local.
// unchipped scientist kill is shared. chipped get wonchar on entering the end lab.
// chipped only pop if an unchipped player actually starts the escape. lobby goes back to the menu once every receipt is done.
public static class ConcurrentEndings {
    public static bool ScientistDead;
    public static bool LocalMercResolved;
    public static bool LocalChipDetonateStarted;
    public static bool LocalWoncharGrantedForEndLab;
    public static bool ApplyingScientistShot;
    // dead chipped/merc finale already played. blocks end-lab from healing them into another lore + receipt loop.
    public static bool FinaleLatched;
    public static bool SuppressDeathScreen;
    public static bool KeepSpectating;
    // one biopod receipt after the chipped lore card, then spectator
    public static bool AllowChippedReceipt;
    // receipt is up on this machine
    public static bool EndingCardVisible;
    // biome 10 must not autocontinue. one player at the top starts the scroller for everyone.
    // the lab starts after every living player finishes reading.
    static readonly HashSet<knetid> _ascentDone = new HashSet<knetid>();
    // "got an ending" and "finished the receipt" happen minutes apart. menu wait needs both, which is annoying.
    private static readonly HashSet<knetid> _endingReceived = new HashSet<knetid>();
    private static readonly HashSet<knetid> _cutsceneFinished = new HashSet<knetid>();
    private static bool _unchippedEscapeFinished;
    private static bool _lobbyReturnStarted;
    static bool _ascentCutsceneStarted;
    static float _surgeryTpNext;
    static bool _anestheticBagsSpawned;
    static readonly Vector2 SurgeryEntrance = new Vector2(250f, 234f);
    static readonly Vector2 SurgeryRoom = new Vector2(287f, 243f);
    // liquids.localanesthetic: duration = 60 * (ml * 0.1) = 6 per ml. stock needed >50 (~8.3ml).
    public const float SurgeryAnestheticMlRequired = 1f;
    public static float SurgeryAnestheticDurationRequired => 60f * (SurgeryAnestheticMlRequired * 0.1f);

    public static bool IsLocalUnchipped() {
        if (ScavPlayer.LocalPlayer != null) {
            return ScavPlayer.LocalPlayer.IsUnchipped();
        }
        return Util.IsUnchipped();
    }

    public static bool LocalHasMercScarf() {
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        return body != null && body.FindByIdThorough("mercscarf", out _);
    }

    public static void GrantWoncharForLocalCharacter() {
        int chr = -1;
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null) {
            chr = CharacterSpawn.ClampChar(body.charType);
        } else if (ScavPlayer.LocalPlayer != null) {
            chr = CharacterSpawn.ClampChar(ScavPlayer.LocalPlayer.selectedCharacter);
        }
        if (chr < 0) {
            return;
        }
        PlayerPrefs.SetInt("wonchar" + chr, 1);
        SteamManager.TryUnlockAchievement("CHARWIN" + chr);
    }

    // unchipped biopod/escape finished: every peer unlocks the character they brought.
    public static void AnnounceEndingCredit() {
        if (!Net.IsRunning) {
            return;
        }
        if (Net.IsServer) {
            SendEndingCredit(ServerMain.AllClientIdsExceptHost);
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_EndingCredit);
        writer.Put(true);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    public static void SendEndingCredit(System.Collections.Generic.IReadOnlyList<knetid> who) {
        if (who == null || who.Count == 0) {
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_EndingCredit);
        writer.Put(true);
        Net.Server_SendToClientsVeryReliable(writer, who);
    }

    // chipped players entering End Lab get completion credit immediately
    public static void TryGrantEndLabWoncharEarly() {
        if (!Net.IsRunning || LocalWoncharGrantedForEndLab) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
            return;
        }
        if (IsLocalUnchipped()) {
            return;
        }
        LocalWoncharGrantedForEndLab = true;
        GrantWoncharForLocalCharacter();
        FindTheBugInator.Mark("endlab-wonchar-early");
    }

    public static void ResetLayerLocalState() {
        ScientistDead = false;
        LocalMercResolved = false;
        LocalChipDetonateStarted = false;
        FinaleLatched = false;
        SuppressDeathScreen = false;
        KeepSpectating = false;
        AllowChippedReceipt = false;
        EndingCardVisible = false;
        _endingReceived.Clear();
        _cutsceneFinished.Clear();
        _unchippedEscapeFinished = false;
        _lobbyReturnStarted = false;
        MacroFleshRebuilderMp.ResetLayer();
        ScientistGate.Reset();
        _ascentDone.Clear();
        _ascentCutsceneStarted = false;
        _surgeryTpNext = 0f;
        _anestheticBagsSpawned = false;
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
            LocalWoncharGrantedForEndLab = false;
        }
    }

    public static void ApplyScientistDeathVisuals(EndLabScript lab) {
        if (lab == null) {
            return;
        }
        ScientistDead = true;
        if (lab.scientistCoroutine != null) {
            lab.StopCoroutine(lab.scientistCoroutine);
            lab.scientistCoroutine = null;
        }
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = "";
        }
        lab.doingEscape = true;
        if (lab.scientistAnim != null) {
            lab.scientistAnim.enabled = true;
            lab.scientistAnim.Play("ScientistShot");
            Collider2D col = lab.scientistAnim.GetComponent<Collider2D>();
            if (col != null) {
                col.enabled = false;
            }
            Utils.Create("HumanBlood", lab.scientistAnim.transform.position, 0f);
        }
        if (lab.scientistTalker != null) {
            lab.scientistTalker.Skip();
        }
        Sound.Play("cut5", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        if (IsLocalUnchipped()) {
            PlayerCamera cam = PlayerCamera.main;
            if (cam != null) {
                cam.blockItemUsage = false;
                if (cam.body != null) {
                    cam.body.forceWalk = false;
                    cam.body.forceNoMovement = false;
                }
            }
            if (WorldGeneration.world != null) {
                WorldGeneration.world.cutsceneActive = false;
            }
            SharedMain.IsInCutscene = false;
        }
        if (MusicManager.main != null) {
            MusicManager.main.cutsceneVolMult = 1f;
            MusicManager.main.StopSong();
            MusicManager.main.StartCoroutine(MusicManager.main.EscapeMusic());
        }
        EndingCarry.DropIllegal();
    }

    // standing near (250, 234) is the teleport into the macro flesh room.
    // the host owns the body position, so the client asks and then waits until that move arrives.
    static bool SurgeryDoorPulls(Body body) {
        if (body == null || !body.TryGetNetBody(out NetBody nb) || nb == null) {
            return false;
        }
        if (nb.plr != null) {
            return nb.plr.IsUnchipped();
        }
        return nb.unchipped;
    }

    public static void HostTeleportIntoSurgery(Body body) {
        if (!Net.IsServer || body == null || !body.alive || !SurgeryDoorPulls(body)) {
            return;
        }
        if (Vector2.Distance(body.transform.position, SurgeryRoom) < 4f) {
            return;
        }
        if (body.TryGetNetBody(out NetBody nb)) {
            nb.SetBodyPosition(SurgeryRoom);
            body.SetVelocity(Vector2.zero);
            nb.Server_RemindPlayersCurrentState(true);
        } else {
            body.transform.position = SurgeryRoom;
            body.SetVelocity(Vector2.zero);
        }
        if (PlayerCamera.main != null && PlayerCamera.main.body == body) {
            PlayerCamera.main.transform.position = body.transform.position;
        }
        FindTheBugInator.Mark("surgery-tp");
    }

    // an unchipped player through the surgery door pulls the other unchipped players. chipped stay put.
    public static void HostTeleportAllIntoSurgery() {
        if (!Net.IsServer) {
            return;
        }
        foreach (var pair in ScavPlayer.BodyToPlayerDict) {
            HostTeleportIntoSurgery(pair.Key);
        }
        FindTheBugInator.Mark("surgery-tp-all");
    }

    static bool AnyPlayerNearSurgeryDoor() {
        foreach (var pair in ScavPlayer.BodyToPlayerDict) {
            Body b = pair.Key;
            if (b == null || !b.alive) {
                continue;
            }
            Vector2 p = b.transform.position;
            if (Vector2.Distance(p, SurgeryEntrance) < 10f || Vector2.Distance(p, SurgeryRoom) < 6f) {
                return true;
            }
        }
        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (local == null || !local.alive) {
            return false;
        }
        Vector2 lp = local.transform.position;
        return Vector2.Distance(lp, SurgeryEntrance) < 10f || Vector2.Distance(lp, SurgeryRoom) < 6f;
    }

    static void RequestSurgeryTeleport(Body body) {
        if (body == null) {
            return;
        }
        if (Net.IsServer) {
            HostTeleportAllIntoSurgery();
            return;
        }
        if (Time.unscaledTime < _surgeryTpNext) {
            return;
        }
        _surgeryTpNext = Time.unscaledTime + 0.75f;
        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put("surgery_tp");
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    // stock drops one filled injector. MP: one additional bag per lobby player at that spawn.
    public static void HostSpawnExtraAnestheticBags(EndLabScript lab) {
        if (!Net.IsServer || lab == null || lab.injector == null || _anestheticBagsSpawned) {
            return;
        }
        _anestheticBagsSpawned = true;
        int players = ScavPlayer.BodyToPlayerDict.Count;
        if (players < 1) {
            players = 1;
        }
        GameObject src = lab.injector.gameObject;
        Vector3 basePos = src.transform.position;
        Quaternion rot = src.transform.rotation;
        for (int i = 0; i < players; i++) {
            // ring them around the stock bag so they don't sit inside its sprite
            float angle = (i + 0.5f) * (Mathf.PI * 2f / players);
            Vector3 pos = basePos + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * 0.85f;
            GameObject go = UnityEngine.Object.Instantiate(src, pos, rot);
            // the clone copies the scene bag's tracker. destroy it now, before Start
            // reuses that id and the new bag never gets its own sync.
            SyncInfoGameObjectTracker[] trackers = go.GetComponentsInChildren<SyncInfoGameObjectTracker>(true);
            for (int t = 0; t < trackers.Length; t++) {
                UnityEngine.Object.DestroyImmediate(trackers[t]);
            }
            FreshItemDrop existingDrop = go.GetComponent<FreshItemDrop>();
            if (existingDrop != null) {
                UnityEngine.Object.Destroy(existingDrop);
            }
            WaterContainerItem wci = go.GetComponent<WaterContainerItem>();
            if (wci != null) {
                wci.DrainAll();
                wci.AddLiquid("localanesthetic", 347f);
            }
            FreshItemDrop drop = go.AddComponent<FreshItemDrop>();
            drop.forceOn = true;
            NetObjectRegistry.SafeNewGO(go);
        }
        FindTheBugInator.Mark("surgery-anes-bags", players.ToString());
    }

    public static bool HasEnoughAbdomenAnesthetic() {
        float need = SurgeryAnestheticDurationRequired;
        CoUtils local = CoUtils.instance;
        if (local != null && local.DurationOf("localanestheticDownTorso") >= need) {
            return true;
        }
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) {
            return false;
        }
        CoUtils onBody = body.GetComponent<CoUtils>();
        if (onBody == null) {
            return false;
        }
        if (onBody.DurationOf("localanestheticDownTorso") >= need) {
            return true;
        }
        if (body.limbs != null && body.limbs.Length > 2 && body.limbs[2] != null) {
            string named = "localanesthetic" + body.limbs[2].name;
            if (onBody.DurationOf(named) >= need) {
                return true;
            }
        }
        return false;
    }

    public static IEnumerator MpEscapeToSurgery(EndLabScript lab) {
        yield return new WaitForSeconds(5f);
        if (lab.announcer != null) {
            lab.announcer.Talk(Locale.GetOther("escapealert"));
        }
        // any lobby player entering the surgery door starts the room pull for everyone
        yield return new WaitUntil(AnyPlayerNearSurgeryDoor);
        if (GlobalDark.main != null) {
            GlobalDark.main.Darken();
            yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        }
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        float waited = 0f;
        while (body != null && Vector2.Distance(body.transform.position, SurgeryRoom) > 4f) {
            if (waited <= 0f) {
                RequestSurgeryTeleport(body);
                waited = 0.75f;
            }
            waited -= Time.deltaTime;
            yield return null;
            body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        }
        if (body == null || lab == null) {
            yield break;
        }
        if (PlayerCamera.main != null) {
            PlayerCamera.main.transform.position = body.transform.position;
        }
        if (lab.endGuardSpawner != null) {
            lab.endGuardSpawner.openProgress = 1.45f;
        }
        Sound.Play("endlabDoor", body.transform.position, twoDimensional: false, pitchShift: false);
        lab.doTimer = true;
        if (MusicManager.main != null) {
            MusicManager.main.cutsceneVolMult = 1f;
            MusicManager.main.specialLoop = true;
            MusicManager.main.PlaySong(lab.end1);
        }
        if (lab.injector != null) {
            FreshItemDrop drop = lab.injector.GetComponent<FreshItemDrop>();
            if (drop == null) {
                drop = lab.injector.gameObject.AddComponent<FreshItemDrop>();
            }
            drop.forceOn = true;
            if (Net.IsServer) {
                HostSpawnExtraAnestheticBags(lab);
            } else {
                NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
                writer.Put("surgery_bags");
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            }
        }
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery1");
        }
        yield return new WaitUntil(HasEnoughAbdomenAnesthetic);
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery2");
        }
        lab.canStartSurgery = true;
        yield return new WaitUntil(() => (bool)MinigameBase.main.currentMinigame && MinigameBase.main.currentMinigame is CargoMinigame);
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery3");
        }
        CargoMinigame minigame = MinigameBase.main.currentMinigame as CargoMinigame;
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return ((bool)minigame && minigame.stage == 1) || (b != null && !b.hasCargo);
        });
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery4");
        }
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return b != null && !b.hasCargo;
        });
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery5");
        }
        if (lab.removedHeart != null) {
            lab.removedHeart.SetActive(true);
        }
        yield return new WaitUntil(() => MinigameBase.main == null || !MinigameBase.main.currentMinigame);
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery6");
        }
        if (MusicManager.main != null) {
            MusicManager.main.PlaySong(lab.end2);
        }
        if (lab.surgicalDevice != null) {
            MacroFleshRebuilderMp.EnsureSurgicalDeviceUsable();
        }
        lab.doTimer = false;
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return b != null && b.transform.position.x > 353f;
        });
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = "";
        }
        body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) {
            yield break;
        }
        body.forceWalk = true;
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return b != null && b.transform.position.x > 366f;
        });
        body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) {
            yield break;
        }
        body.forceWalk = false;
        body.Ragdoll();
        body.shock = 50f;
        body.Scream();
        if (lab.doorJoint != null) {
            UnityEngine.Object.Destroy(lab.doorJoint);
        }
        Sound.Play("robothit", body.transform.position, twoDimensional: false, pitchShift: false);
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return b != null && b.transform.position.y > 270f && b.transform.position.x < 370f;
        });
        if (GlobalDark.main != null) {
            GlobalDark.main.Darken();
            yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        }
        body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null) {
            body.forceNoMovement = true;
        }
        if (WorldGeneration.world != null) {
            WorldGeneration.world.StartCoroutine(WorldGeneration.world.EscapeEnding());
        }
    }

    public static void OnScientistShotLocal() {
        EndLabScript lab = EndLabScript.instance;
        if (lab == null) {
            return;
        }
        bool already = ScientistDead && lab.doingEscape;
        ApplyScientistDeathVisuals(lab);
        FindTheBugInator.Mark("scientist-shot", already ? "already" : "first");
        if (already) {
            return;
        }

        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null) {
            body.forceWalk = false;
            body.talker.Talk(Locale.GetCharacter("labshoot"), null, force: false, resetTalkTimer: true);
        }

        if (IsLocalUnchipped()) {
            bool alive = body != null && body.alive;
            if (alive && lab.escapeCoroutine == null) {
                lab.escapeCoroutine = lab.StartCoroutine(MpEscapeToSurgery(lab));
            } else if (!alive) {
                ReportEnding("done");
            }
        } else if (!LocalChipDetonateStarted) {
            // only an unchipped player's shot reaches here. a chipped walk-out is not a detonation.
            LocalChipDetonateStarted = true;
            lab.StartCoroutine(DelayedChipDetonate());
        }
    }

    public static void LatchDeadFinale() {
        FinaleLatched = true;
        SuppressDeathScreen = true;
        FindTheBugInator.Mark("finale-latch");
        // end lab restarts a biopod ending whenever this is clear and the brain is dying
        if (WorldGeneration.world != null) {
            WorldGeneration.world.cutsceneActive = true;
        }
    }

    public static void EnterFinaleSpectator() {
        KeepSpectating = true;
        SuppressDeathScreen = true;
        FindTheBugInator.Mark("finale-spectate");
        if (PlayerCamera.main != null && PlayerCamera.main.endScreen != null) {
            PlayerCamera.main.endScreen.gameObject.SetActive(false);
        }
        if (!UIInGame.SpectatorModeActive) {
            UIInGame.StartSpectatorMode();
        }
    }

    public static IEnumerator EmptyFinale() {
        yield break;
    }

    // already dead: one lore card, then spectator. no receipt, no heal, no second pass.
    public static IEnumerator DeadPlayerLoreThenSpectate(string loreKey) {
        LatchDeadFinale();
        ReportEnding("recv");
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null && body.alive && PlayerCamera.main != null) {
            PlayerCamera.main.StartSelfDestruct();
            float wait = 8f;
            while (wait > 0f && body.alive) {
                wait -= Time.unscaledDeltaTime;
                yield return null;
            }
        }
        if (PlayerCamera.main != null && PlayerCamera.main.endScreen != null) {
            PlayerCamera.main.endScreen.gameObject.SetActive(false);
        }
        if (WorldGeneration.world != null) {
            WorldGeneration.world.cutsceneActive = true;
        }
        SharedMain.IsInCutscene = true;
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        ScrollableText.CreateText(Locale.GetOther(loreKey), cover: true);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        WorldGeneration world = WorldGeneration.world;
        if (world != null && world.cutsceneUnder != null && world.cutsceneImages != null && world.cutsceneImages.Length > 7) {
            world.cutsceneUnder.sprite = world.cutsceneImages[7];
        }
        if (MusicManager.main != null) {
            MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/biopod"));
            MusicManager.main.specialLoop = false;
        }
        ScrollableText.closeType = ScrollableText.TextCloseType.Instant;
        PlayerCamera cam = PlayerCamera.main;
        if (cam != null) {
            AllowChippedReceipt = true;
            // endSequence is replaced while latched unless this flag is set. the wrapper spectates after the click.
            yield return cam.StartCoroutine(cam.EndSequence(1));
        }
        if (!UIInGame.SpectatorModeActive) {
            EnterFinaleSpectator();
        }
    }

    static IEnumerator DelayedChipDetonate() {
        // receipt must not cover the lore that plays after the pop
        LatchDeadFinale();
        yield return new WaitForSecondsRealtime(Random.Range(1f, 5f));
        string loreKey = "biopodnotealt";
        EndLabScript lab = EndLabScript.instance;
        if (lab != null && lab.doingEscape) {
            loreKey = "biopodnotealt2";
        }
        yield return DeadPlayerLoreThenSpectate(loreKey);
    }

    // own biopod, nobody shot the scientist. lore then the receipt. pop only happens if the escape already started.
    public static IEnumerator ChippedBiopodDialogue(string loreKey) {
        LatchDeadFinale();
        ReportEnding("recv");
        if (ScientistDead && !LocalChipDetonateStarted) {
            LocalChipDetonateStarted = true;
            Body dying = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (dying != null && dying.alive && PlayerCamera.main != null) {
                PlayerCamera.main.StartSelfDestruct();
            }
        }
        WorldGeneration world = WorldGeneration.world;
        if (world != null) {
            world.cutsceneActive = true;
        }
        SharedMain.IsInCutscene = true;
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        ScrollableText.CreateText(Locale.GetOther(loreKey), cover: true);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        if (world != null && world.cutsceneUnder != null && world.cutsceneImages != null && world.cutsceneImages.Length > 7) {
            world.cutsceneUnder.sprite = world.cutsceneImages[7];
        }
        if (MusicManager.main != null) {
            MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/biopod"));
            MusicManager.main.specialLoop = false;
        }
        ScrollableText.closeType = ScrollableText.TextCloseType.Instant;
        PlayerCamera cam = PlayerCamera.main;
        if (cam != null) {
            AllowChippedReceipt = true;
            yield return cam.StartCoroutine(cam.EndSequence(1));
        }
        if (ScientistDead && !UIInGame.SpectatorModeActive) {
            EnterFinaleSpectator();
        }
    }

    // a blackout used to start the chip ending. hold the lab check until they wake, or until a real escape starts.
    public static IEnumerator IgnoreEarlyBrainBiopod() {
        WorldGeneration world = WorldGeneration.world;
        if (world == null) {
            yield break;
        }
        bool changed = false;
        if (!world.cutsceneActive) {
            world.cutsceneActive = true;
            changed = true;
        }
        while (!ScientistDead) {
            Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            bool dying = body != null && body.bloodPressure < 10f && body.consciousness < 5f;
            if (!dying) {
                break;
            }
            yield return null;
        }
        if (changed && !ScientistDead && !FinaleLatched) {
            world.cutsceneActive = false;
        }
    }

    public static IEnumerator MpMercEnding(WorldGeneration world) {
        LatchDeadFinale();
        ReportEnding("recv");
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        Time.timeScale = 1f;
        world.cutsceneActive = true;
        SharedMain.IsInCutscene = true;
        Body body = PlayerCamera.main.body;
        body.forceNoMovement = true;
        MusicManager.main.StopSong();
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        ScrollableText.CreateText(Locale.GetOther("mercnote1"), cover: true);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        GlobalDark.main.SetSpeedMultiplier(0.1f);
        world.cutsceneUnder.gameObject.SetActive(true);
        world.cutsceneUnder.sprite = world.cutsceneImages[9];
        yield return new WaitForSecondsRealtime(3f);
        yield return new WaitUntil(() => Input.GetKeyDown(KeyBinds.GetBind("attack")));
        GlobalDark.main.SetSpeedMultiplier(1f);
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        ScrollableText.CreateText(Locale.GetOther("mercnote2"), cover: true);
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/endlabdrone"));
        MusicManager.main.specialLoop = true;
        world.cutsceneUnder.gameObject.SetActive(false);
        world.cutsceneOver.gameObject.SetActive(false);
        world.cutsceneUnder.sprite = world.cutsceneImages[11];
        yield return new WaitUntil(() => ScrollableText.onLastPanel || !ScrollableText.textExists || GlobalDark.main.IsDarkening());
        MusicManager.main.StopSong();
        float timer = 5f;
        while (timer > 0f && ScrollableText.textExists && !GlobalDark.main.IsDarkening()) {
            timer -= Time.unscaledDeltaTime;
            yield return null;
        }
        Sound.Play("heartsplosion", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        GlobalDark.main.SetDarkAmount(1f);
        GlobalDark.main.SetStayDark(stay: true);
        GlobalDark.main.Darken();
        yield return null;
        ScrollableText.ForceClose();
        yield return new WaitForSecondsRealtime(4f);
        GlobalDark.main.SetStayDark(stay: false);
        ScrollableText.CreateText(Locale.GetOther("mercnote3"), cover: true);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/biopod"));
        MusicManager.main.specialLoop = false;
        SteamManager.TryUnlockAchievement("TOBECONTINUED");
        GrantWoncharForLocalCharacter();
        PlayerPrefs.SetInt("didmerc", 1);

        world.cutsceneActive = false;
        SharedMain.IsInCutscene = false;
        body.forceNoMovement = false;
        LocalMercResolved = true;

        if (PlayerCamera.main != null) {
            PlayerCamera.main.StartSelfDestruct();
        }
        yield return new WaitForSecondsRealtime(5f);
        ReportEnding("done");
        EnterFinaleSpectator();
    }

    public static IEnumerator MpSurfaceBreachThenRequestLab(WorldGeneration world) {
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        Time.timeScale = 1f;
        world.cutsceneActive = true;
        SharedMain.IsInCutscene = true;
        yield return new WaitUntil(() => PlayerCamera.main != null && PlayerCamera.main.body != null);
        Body body = PlayerCamera.main.body;
        body.forceNoMovement = true;
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        ScrollableText.CreateText(Locale.GetOther("surfacebreachnote"), cover: true);
        MusicManager.main.cutsceneVolMult = 1f;
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/surfacebreachA"));
        MusicManager.main.specialLoop = true;
        yield return new WaitUntil(() => !ScrollableText.textExists);
        GlobalDark.main.SetSpeedMultiplier(0.1f);
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/surfacebreachB"));
        MusicManager.main.specialLoop = false;
        world.cutsceneUnder.gameObject.SetActive(true);
        world.cutsceneUnder.sprite = world.cutsceneImages[6];
        world.cutsceneOver.gameObject.SetActive(true);
        world.cutsceneOver.sprite = world.cutsceneImages[body.charType];
        yield return new WaitForSecondsRealtime(3f);
        yield return new WaitUntil(() => Input.GetKeyDown(KeyBinds.GetBind("attack")));
        GlobalDark.main.SetSpeedMultiplier(1f);
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        ScrollableText.CreateText(Locale.GetOther("labenternote"), cover: true);
        world.cutsceneUnder.gameObject.SetActive(false);
        world.cutsceneOver.gameObject.SetActive(false);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        MusicManager.main.StopSong();
        world.cutsceneActive = false;
        SharedMain.IsInCutscene = false;
        body.forceNoMovement = false;
        body.forceWalk = true;

        RequestAdvanceToEndLab();
    }

    // host: first living body at the ascent top starts the breach scroller on every peer.
    public static void Server_TryStartAscentCutscene() {
        if (!Net.IsServer || _ascentCutsceneStarted) {
            return;
        }
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.biomeDepth != 10 || world.doingRegen || world.generatingWorld || !world.worldExists) {
            return;
        }
        bool reached = false;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null || !plr.body.alive) {
                continue;
            }
            if (FullgameWorldCompat.IsAtLayerExit(plr.body)) {
                reached = true;
                break;
            }
        }
        if (!reached) {
            return;
        }
        _ascentCutsceneStarted = true;
        FindTheBugInator.Mark("ascent-go");
        BeginAscentScroller();
        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put("ascent_go");
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
    }

    // the person who reached the top starts the breach music on their own machine.
    // the host packet still starts it for everyone else.
    public static void TickLocalAscent() {
        if (!Net.IsRunning || Net.IsServer) {
            return;
        }
        if (!FullgameWorldCompat.IsAscentLayer()) {
            return;
        }
        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (local == null || !local.alive || !FullgameWorldCompat.IsAtLayerExit(local)) {
            return;
        }
        BeginAscentScroller();
    }

    public static void BeginAscentScroller() {
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.biomeDepth != 10) {
            return;
        }
        Traverse fields = Traverse.Create(world);
        Coroutine running = fields.Field("cutsceneCoroutine").GetValue<Coroutine>();
        if (running != null) {
            return;
        }
        Coroutine started = world.StartCoroutine(MpSurfaceBreachThenRequestLab(world));
        fields.Field("cutsceneCoroutine").SetValue(started);
    }

    public static void RequestAdvanceToEndLab() {
        if (!Net.IsRunning || WorldGeneration.world == null) {
            return;
        }
        if (Net.IsServer) {
            if (ScavPlayer.LocalPlayer != null) {
                MarkAscentDone(ScavPlayer.LocalPlayer.playerId);
            }
            Server_TryAdvanceToEndLab();
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put("endlab_ready");
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    static void MarkAscentDone(knetid playerId) {
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 10) {
            return;
        }
        _ascentDone.Add(playerId);
    }

    static bool AllLivingFinishedAscent() {
        bool any = false;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null || !plr.body.alive) {
                continue;
            }
            any = true;
            if (!_ascentDone.Contains(plr.playerId)) {
                return false;
            }
        }
        return any;
    }

    public static void Server_TryAdvanceToEndLab() {
        if (!Net.IsServer || WorldGeneration.world == null) {
            return;
        }
        var world = WorldGeneration.world;
        if (world.biomeDepth != 10) {
            return;
        }
        bool doingRegen = (bool)Traverse.Create(world).Field("doingRegen").GetValue();
        if (doingRegen || world.generatingWorld) {
            return;
        }
        if (!AllLivingFinishedAscent()) {
            FindTheBugInator.Mark("endlab-wait-ascent");
            Multiplayer.DoMultiplayerStatusMessageLog("ConcurrentEndings: waiting for every living player to finish the ascent cutscene");
            return;
        }
        FindTheBugInator.Mark("endlab-advance");
        Multiplayer.DoMultiplayerStatusMessageLog("ConcurrentEndings: advancing Crimson Ascent → End Lab");
        PlayerCheckpoint.WriteLayerTransition();
        world.StartCoroutine(world.RegenerateWorld());
    }

    // ending_flag words: recv = started, done = clicked through the receipt, escape = unchipped got out alive.
    public static void ReportEnding(string which) {
        if (!Net.IsRunning || ScavPlayer.LocalPlayer == null) {
            return;
        }
        if (Net.IsServer) {
            NoteEnding(ScavPlayer.LocalPlayer, which);
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put("ending_flag " + which);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    private static void NoteEnding(ScavPlayer plr, string which) {
        if (!Net.IsServer || plr == null) {
            return;
        }
        FindTheBugInator.Mark("flag-" + which, "player=" + (plr.playerName ?? "?"));
        if (which == "recv") {
            _endingReceived.Add(plr.playerId);
        } else if (which == "done" || which == "escape") {
            _endingReceived.Add(plr.playerId);
            _cutsceneFinished.Add(plr.playerId);
            if (which == "escape") {
                _unchippedEscapeFinished = true;
            }
        }
        TryReturnLobbyIfReady();
    }

    // dead players who never started an ending don't hold the lobby. anyone who did has to finish the receipt.
    private static bool MustFinishEnding(ScavPlayer plr) {
        if (plr == null) {
            return false;
        }
        if (_endingReceived.Contains(plr.playerId) || _cutsceneFinished.Contains(plr.playerId)) {
            return true;
        }
        return plr.body != null && plr.body.alive;
    }

    public static bool AllCutscenesFinished() {
        bool any = false;
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (!MustFinishEnding(plr)) {
                continue;
            }
            any = true;
            if (!_cutsceneFinished.Contains(plr.playerId)) {
                return false;
            }
        }
        return any;
    }

    private static bool AllEndingsReady() {
        bool any = false;
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (!MustFinishEnding(plr)) {
                continue;
            }
            any = true;
            if (!_endingReceived.Contains(plr.playerId) || !_cutsceneFinished.Contains(plr.playerId)) {
                return false;
            }
        }
        return any;
    }

    // host return stays off while anyone still has a cutscene going. normal deaths before that still get the button.
    public static bool BlockHostReturn() {
        if (!Net.IsRunning || !Net.IsServer) {
            return false;
        }
        if (!ScientistDead && !EndingCardVisible && _endingReceived.Count == 0) {
            return false;
        }
        return !AllCutscenesFinished();
    }

    public static void TryReturnLobbyIfReady() {
        if (!Net.IsServer || _lobbyReturnStarted || !Net.IsRunning) {
            return;
        }
        if (!ScientistDead && !_unchippedEscapeFinished) {
            return;
        }
        if (!AllEndingsReady()) {
            return;
        }
        _lobbyReturnStarted = true;
        FindTheBugInator.Mark("lobby-return");
        if (PlayerCamera.main != null) {
            PlayerCamera.main.ToMainMenu();
        }
    }

    [CTPostInit]
    static void Init() {
        ClientMain.ClientCustomCommandsDict["ascent_go"] = (cmd, reader) => {
            BeginAscentScroller();
        };
        ServerMain.ServerCustomCommandsDict["endlab_ready"] = (plr, cmd, args) => {
            if (plr != null) {
                MarkAscentDone(plr.playerId);
            }
            Server_TryAdvanceToEndLab();
        };
        ServerMain.ServerCustomCommandsDict["cargo_off"] = (plr, cmd, args) => {
            MacroFleshRebuilderMp.HostAcceptHeartRemoved(plr);
        };
        ServerMain.ServerCustomCommandsDict["blueheartremoved"] = (plr, cmd, args) => {
            MacroFleshRebuilderMp.HostAcceptHeartRemoved(plr);
        };
        ClientMain.ClientCustomCommandsDict["blueheartremoved"] = (cmd, reader) => {
            MacroFleshRebuilderMp.GrantLocalEscape();
        };
        ServerMain.ServerCustomCommandsDict["ending_flag"] = (plr, cmd, args) => {
            string which = args.Length > 1 ? args[1] : "done";
            NoteEnding(plr, which);
        };
        ServerMain.ServerCustomCommandsDict["surgery_tp"] = (plr, cmd, args) => {
            if (plr == null || plr.body == null || !plr.body.alive) {
                return;
            }
            if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
                return;
            }
            // unchipped door entry pulls the other unchipped players
            HostTeleportAllIntoSurgery();
        };
        ServerMain.ServerCustomCommandsDict["surgery_bags"] = (plr, cmd, args) => {
            if (plr == null || plr.body == null || !plr.body.alive || !plr.IsUnchipped()) {
                return;
            }
            if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
                return;
            }
            if (Vector2.Distance(plr.body.transform.position, SurgeryRoom) > 12f
                && Vector2.Distance(plr.body.transform.position, SurgeryEntrance) > 16f) {
                return;
            }
            EndLabScript lab = EndLabScript.instance;
            if (lab == null) {
                return;
            }
            HostSpawnExtraAnestheticBags(lab);
        };
        WorldMP.OnWorldgenFinish += () => {
            ResetLayerLocalState();
            TryGrantEndLabWoncharEarly();
        };
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.MercEnding))]
static class WorldGeneration_MercEnding_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = ConcurrentEndings.MpMercEnding(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SurfaceBreachCutscene))]
static class WorldGeneration_SurfaceBreachCutscene_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = ConcurrentEndings.MpSurfaceBreachThenRequestLab(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(EndLabScript), nameof(EndLabScript.BeginEscapeSequence))]
static class EndLabScript_BeginEscapeSequence_MultiplayerPatch {
    static bool UnchippedGunman() {
        NetBody shooter = TurretScript_Shoot_MultiplayerPatch.LastActiveShooter;
        if (shooter == null) {
            return false;
        }
        if (shooter.plr != null) {
            return shooter.plr.IsUnchipped();
        }
        return shooter.unchipped;
    }

    static bool Prefix(EndLabScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (ConcurrentEndings.ApplyingScientistShot) {
            return false;
        }
        if (__instance.doingEscape || ConcurrentEndings.ScientistDead) {
            return false;
        }
        // a turret or a chipped player's bullet must not start the escape or pop anyone's chip.
        if (!UnchippedGunman()) {
            return false;
        }

        if (Net.IsServer) {
            BroadcastScientistShot();
            ConcurrentEndings.ApplyingScientistShot = true;
            try {
                ConcurrentEndings.OnScientistShotLocal();
            } finally {
                ConcurrentEndings.ApplyingScientistShot = false;
            }
        } else {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ScientistShot);
            writer.Put(true);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }
        return false;
    }

    static void BroadcastScientistShot() {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_ScientistShot);
        writer.Put(true);
        Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ScientistShot)]
    static void Server_ScientistShot(knetid clientId, NetDataReader reader) {
        if (ConcurrentEndings.ScientistDead) {
            return;
        }
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer shooter) || shooter == null || !shooter.IsUnchipped()) {
            return;
        }
        BroadcastScientistShot();
        ConcurrentEndings.ApplyingScientistShot = true;
        try {
            ConcurrentEndings.OnScientistShotLocal();
        } finally {
            ConcurrentEndings.ApplyingScientistShot = false;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ScientistShot)]
    static void Client_ScientistShot(knetid _, NetDataReader reader) {
        ConcurrentEndings.ApplyingScientistShot = true;
        try {
            ConcurrentEndings.OnScientistShotLocal();
        } finally {
            ConcurrentEndings.ApplyingScientistShot = false;
        }
    }
}

// ending cutscenes stay local - don't mirror cutsceneActive across the lobby on finale depths
[HarmonyPatch(typeof(FullgameMultiplayerPatcher), "Update")]
static class FullgameMultiplayerPatcher_Cutscene_SkipFinaleBroadcast_MultiplayerPatch {
    static bool Prefix() {
        if (!Net.IsRunning || WorldGeneration.world == null) {
            return true;
        }
        if (WorldGeneration.world.biomeDepth >= 10) {
            return false;
        }
        return true;
    }
}

// biopod / escape call SetGameWon then EndSequence. credit every peer's character from here.
[HarmonyPatch(typeof(WorldGeneration), "SetGameWon")]
static class WorldGeneration_SetGameWon_MultiplayerPatch {
    static void Postfix() {
        if (!Net.IsRunning) {
            return;
        }
        ConcurrentEndings.AnnounceEndingCredit();
        ConcurrentEndings.ReportEnding("recv");
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.EndSequence))]
static class PlayerCamera_EndSequence_MultiplayerPatch {
    static void Postfix(int type, ref IEnumerator __result) {
        if (!Net.IsRunning || __result == null) {
            return;
        }
        if (type == 0) {
            if (ConcurrentEndings.SuppressDeathScreen) {
                __result = ConcurrentEndings.EmptyFinale();
            }
            return;
        }
        bool chippedReceipt = type == 1 && ConcurrentEndings.AllowChippedReceipt;
        if (chippedReceipt) {
            ConcurrentEndings.AllowChippedReceipt = false;
        }
        if (ConcurrentEndings.FinaleLatched && !ConcurrentEndings.IsLocalUnchipped() && !chippedReceipt) {
            __result = ConcurrentEndings.EmptyFinale();
            return;
        }
        IEnumerator inner = __result;
        __result = AfterEndingReceipt(inner, type);
    }

    private static IEnumerator AfterEndingReceipt(IEnumerator inner, int type) {
        ConcurrentEndings.EndingCardVisible = true;
        while (inner.MoveNext()) {
            yield return inner.Current;
        }
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null) {
            body.forceNoMovement = false;
            body.forceWalk = false;
        }
        SharedMain.IsInCutscene = false;
        // type 1 kills you only if the scientist is already dead. otherwise the receipt is just the card.
        bool chipDeath = type == 1 && ConcurrentEndings.ScientistDead && !ConcurrentEndings.IsLocalUnchipped();
        if (chipDeath && body != null && body.alive) {
            ConcurrentEndings.LatchDeadFinale();
            if (PlayerCamera.main != null) {
                PlayerCamera.main.StartSelfDestruct();
            }
            yield return new WaitForSecondsRealtime(5f);
            ConcurrentEndings.EnterFinaleSpectator();
        }
        if (type == 2) {
            ConcurrentEndings.ReportEnding("escape");
        } else {
            ConcurrentEndings.ReportEnding("done");
        }
        ConcurrentEndings.TryReturnLobbyIfReady();
    }
}

static class ChippedBiopodEnding_Once_MultiplayerPatch {
    public static bool Replace(ref IEnumerator __result, string loreKey) {
        if (!Net.IsRunning) {
            return true;
        }
        if (ConcurrentEndings.IsLocalUnchipped() && !ConcurrentEndings.FinaleLatched) {
            return true;
        }
        if (ConcurrentEndings.FinaleLatched) {
            __result = ConcurrentEndings.EmptyFinale();
            return false;
        }
        // brain death in the lab is not the gun escape. do not pop the chip for it.
        if (loreKey != "biopodnote" && !ConcurrentEndings.ScientistDead) {
            __result = ConcurrentEndings.IgnoreEarlyBrainBiopod();
            return false;
        }
        __result = ConcurrentEndings.ChippedBiopodDialogue(loreKey);
        return false;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.BiopodEnding))]
static class WorldGeneration_BiopodEnding_Once_MultiplayerPatch {
    static bool Prefix(ref IEnumerator __result) => ChippedBiopodEnding_Once_MultiplayerPatch.Replace(ref __result, "biopodnote");
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.BiopodEndingAlt))]
static class WorldGeneration_BiopodEndingAlt_Once_MultiplayerPatch {
    static bool Prefix(ref IEnumerator __result) => ChippedBiopodEnding_Once_MultiplayerPatch.Replace(ref __result, "biopodnotealt");
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.BiopodEndingAlt2))]
static class WorldGeneration_BiopodEndingAlt2_Once_MultiplayerPatch {
    static bool Prefix(ref IEnumerator __result) => ChippedBiopodEnding_Once_MultiplayerPatch.Replace(ref __result, "biopodnotealt2");
}

[HarmonyPatch(typeof(PlayerCamera), "HandleDeathScreen")]
static class PlayerCamera_HandleDeathScreen_SkipFinale_MultiplayerPatch {
    static bool Prefix() {
        return !Net.IsRunning || !ConcurrentEndings.SuppressDeathScreen;
    }
}

[HarmonyPatch(typeof(UIInGame), nameof(UIInGame.StopSpectatorMode))]
static class UIInGame_StopSpectatorMode_KeepFinale_MultiplayerPatch {
    static void Postfix() {
        if (!ConcurrentEndings.KeepSpectating || !Net.IsRunning) {
            return;
        }
        if (!Util.IsInWorld()) {
            ConcurrentEndings.KeepSpectating = false;
            return;
        }
        if (!UIInGame.SpectatorModeActive) {
            UIInGame.StartSpectatorMode();
        }
    }
}

[HarmonyPatch]
static class EndingCredit_MultiplayerPatch {
    [ServerReceiver((byte)NetmsgId.SERVER_EndingCredit)]
    static void Server_EndingCredit(knetid clientId, NetDataReader reader) {
        ConcurrentEndings.GrantWoncharForLocalCharacter();
        System.Collections.Generic.List<knetid> others = new System.Collections.Generic.List<knetid>();
        foreach (knetid id in ServerMain.AllClientIdsExceptHost) {
            if (id.id != clientId.id) {
                others.Add(id);
            }
        }
        ConcurrentEndings.SendEndingCredit(others);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_EndingCredit)]
    static void Client_EndingCredit(knetid _, NetDataReader reader) {
        ConcurrentEndings.GrantWoncharForLocalCharacter();
    }
}

// macro flesh rebuilder (SurgicalDeviceBehavior): the minigame runs on the machine that has
// anesthetic. pulling the heart sends blueheartremoved. the host trusts that and opens the
// surgery exit on that player's machine only.
public static class MacroFleshRebuilderMp {
    public static bool LocallyConsumed;
    public static bool CargoClearedLocally;
    public static bool EscapeGranted;
    public static bool EscapeStarted;
    static readonly HashSet<knetid> HeartAccepted = new HashSet<knetid>();
    private static readonly AccessTools.FieldRef<CosmeticDoor, bool> SurgeryDoorOpen = AccessTools.FieldRefAccess<CosmeticDoor, bool>("open");

    public static void ResetLayer() {
        LocallyConsumed = false;
        CargoClearedLocally = false;
        EscapeGranted = false;
        EscapeStarted = false;
        HeartAccepted.Clear();
    }

    public static bool HasRemovedHeart(knetid id) {
        return HeartAccepted.Contains(id);
    }

    public static void HostAcceptHeartRemoved(ScavPlayer plr) {
        if (!Net.IsServer || plr == null) {
            return;
        }
        if (!HeartAccepted.Add(plr.playerId)) {
            return;
        }
        FindTheBugInator.Mark("blueheartremoved", plr.playerName ?? "?");
        if (plr.body != null) {
            EndingCarry.DropFor(plr.netBody);
        }
        if (plr.body != null) {
            if (plr.body.hasCargo) {
                plr.body.RemoveCargo();
            }
            if (plr.body.TryGetNetBody(out NetBody nb)) {
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
        }
        if (plr.IsLocal) {
            GrantLocalEscape();
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put("blueheartremoved");
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
    }

    public static void GrantLocalEscape() {
        if (EscapeGranted) {
            OpenLocalSurgeryDoors(permanent: true);
            return;
        }
        EscapeGranted = true;
        if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.body != null) {
            EndingCarry.DropFor(ScavPlayer.LocalPlayer.netBody);
        }
        EnsureSurgicalDeviceUsable();
        OpenLocalSurgeryDoors(permanent: true);
        EndLabScript lab = EndLabScript.instance;
        if (lab == null) {
            return;
        }
        lab.doTimer = false;
        if (lab.removedHeart != null) {
            lab.removedHeart.SetActive(true);
        }
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery6");
        }
        if (MusicManager.main != null && lab.end2 != null) {
            MusicManager.main.PlaySong(lab.end2);
        }
        FindTheBugInator.Mark("surgery-exit");
    }

    public static void NoteLocalCargoRemoved(Body body) {
        if (body == null || !Util.IsBodyLocal(body)) {
            return;
        }
        CargoClearedLocally = true;
        LocallyConsumed = true;
        if (body.TryGetNetBody(out NetBody carrier)) {
            EndingCarry.DropFor(carrier);
        }
        EnsureSurgicalDeviceUsable();
        if (!Net.IsRunning) {
            GrantLocalEscape();
            return;
        }
        if (Net.IsServer) {
            if (ScavPlayer.LocalPlayer != null) {
                HostAcceptHeartRemoved(ScavPlayer.LocalPlayer);
            }
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
        writer.Put("blueheartremoved");
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    public static bool BlocksCarry(NetBody carrier) {
        if (carrier == null) {
            return false;
        }
        bool local = carrier.IsLocal || (carrier.plr != null && carrier.plr.IsLocal);
        if (local) {
            return LocallyConsumed || CargoClearedLocally || EscapeGranted;
        }
        return Net.IsServer && carrier.plr != null && HeartAccepted.Contains(carrier.plr.playerId);
    }

    public static bool BlockCargoReembed(Body body) {
        if (!CargoClearedLocally || body == null || !Util.IsBodyLocal(body)) {
            return false;
        }
        return true;
    }

    public static void ConsumeLocalPrompt() {
        LocallyConsumed = true;
        EnsureSurgicalDeviceUsable();
    }

    public static void EnsureSurgicalDeviceUsable() {
        SurgicalDeviceBehavior[] devices = UnityEngine.Object.FindObjectsByType<SurgicalDeviceBehavior>(FindObjectsSortMode.None);
        for (int i = 0; i < devices.Length; i++) {
            SurgicalDeviceBehavior device = devices[i];
            if (device == null || device.GetComponent<UsableObject>() != null) {
                continue;
            }
            device.gameObject.AddComponent<UsableObject>();
        }
    }

    public static void OpenLocalSurgeryDoors(bool permanent = false) {
        EndLabScript[] labs = UnityEngine.Object.FindObjectsByType<EndLabScript>(FindObjectsSortMode.None);
        for (int i = 0; i < labs.Length; i++) {
            EndLabScript lab = labs[i];
            if (lab != null && lab.surgeryDoor != null) {
                ForceSurgeryDoorOpen(lab.surgeryDoor, permanent);
            }
        }
    }

    public static void ForceSurgeryDoorOpen(CosmeticDoor door, bool permanent) {
        if (door == null) {
            return;
        }
        door.forceDisable = false;
        if (!permanent) {
            return;
        }
        try {
            SurgeryDoorOpen(door) = true;
        } catch {
        }
        Collider2D col = door.GetComponent<Collider2D>();
        if (col != null) {
            col.enabled = false;
        }
    }

    public static void KeepSurgeryDoorOpenDuringEscape(EndLabScript lab) {
        if (!EscapeGranted || lab == null || lab.surgeryDoor == null) {
            return;
        }
        ForceSurgeryDoorOpen(lab.surgeryDoor, permanent: true);
    }

    // inject writes localanestheticDownTorso onto the body's CoUtils. EscapeCoroutine
    // polls the local singleton, so a real abdomen injection never opened the device.
    public static void MirrorAbdomenAnesthetic(EndLabScript lab) {
        if (lab == null || lab.canStartSurgery || !lab.doTimer) {
            return;
        }
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) {
            return;
        }
        CoUtils onBody = body.GetComponent<CoUtils>();
        if (onBody == null) {
            return;
        }
        const string key = "localanestheticDownTorso";
        float need = ConcurrentEndings.SurgeryAnestheticDurationRequired;
        float left = onBody.DurationOf(key);
        if (left < need && body.limbs != null && body.limbs.Length > 2 && body.limbs[2] != null) {
            string named = "localanesthetic" + body.limbs[2].name;
            if (named != key) {
                left = onBody.DurationOf(named);
            }
        }
        if (left < need) {
            return;
        }
        CoUtils local = CoUtilsMultiplayer.GetLocalCoUtilsInstance();
        if (local == null || local == onBody || local.DurationOf(key) >= need) {
            return;
        }
        local.DoTimedOp(key, () => { }, left);
    }

    public static void TryStartEscapeFromPit(Body body) {
        if (EscapeStarted || !EscapeGranted || body == null || body.hasCargo) {
            return;
        }
        if (WorldGeneration.world == null) {
            return;
        }
        Vector2 p = body.transform.position;
        if (p.y <= 270f || p.x >= 370f) {
            return;
        }
        OpenLocalSurgeryDoors(permanent: true);
        WorldGeneration.world.StartCoroutine(WorldGeneration.world.EscapeEnding());
    }
}

[HarmonyPatch(typeof(SurgicalDeviceBehavior), nameof(SurgicalDeviceBehavior.OnUse))]
static class SurgicalDeviceBehavior_OnUse_MultiplayerPatch {
    static bool Prefix() {
        if (!Net.IsRunning) {
            return true;
        }
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null || body.limbs == null || body.limbs.Length <= 2 || body.limbs[2] == null) {
            return false;
        }
        if (MacroFleshRebuilderMp.EscapeGranted || (MacroFleshRebuilderMp.CargoClearedLocally && !body.hasCargo)) {
            if (PlayerCamera.main != null) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
            }
            return false;
        }
        if (!body.hasCargo || !ConcurrentEndings.HasEnoughAbdomenAnesthetic()) {
            if (PlayerCamera.main != null) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
            }
            return false;
        }
        MacroFleshRebuilderMp.EnsureSurgicalDeviceUsable();
        if (MinigameBase.main != null) {
            MinigameBase.main.StartMinigame(new CargoMinigame(body.limbs[2]), null);
        }
        return false;
    }

    static void Postfix() {
        if (!Net.IsRunning || MinigameBase.main == null || MinigameBase.main.currentMinigame is not CargoMinigame) {
            return;
        }
        MacroFleshRebuilderMp.ConsumeLocalPrompt();
    }
}

// stock CargoMinigame stage 1 does limb.pain += dt*10. when local anesthetic expires that thrash
// breaks the surgery UI - keep the operated limb numb for the whole minigame.
[HarmonyPatch(typeof(CargoMinigame), nameof(CargoMinigame.Update))]
static class CargoMinigame_Update_NoPain_MultiplayerPatch {
    static void Postfix(CargoMinigame __instance) {
        if (!Net.IsRunning || __instance == null || __instance.limb == null) {
            return;
        }
        __instance.limb.pain = 0f;
        __instance.limb.forceMult = 0f;
    }
}


[HarmonyPatch(typeof(EndLabScript), "Update")]
static class EndLabScript_Update_PitEnding_MultiplayerPatch {
    static void Postfix(EndLabScript __instance) {
        if (!Net.IsRunning) {
            return;
        }
        MacroFleshRebuilderMp.KeepSurgeryDoorOpenDuringEscape(__instance);
        if (PlayerCamera.main == null) {
            return;
        }
        MacroFleshRebuilderMp.MirrorAbdomenAnesthetic(__instance);
        MacroFleshRebuilderMp.TryStartEscapeFromPit(PlayerCamera.main.body);
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.EscapeEnding))]
static class WorldGeneration_EscapeEnding_Once_MultiplayerPatch {
    static bool Prefix(ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        if (MacroFleshRebuilderMp.EscapeStarted) {
            __result = Done();
            return false;
        }
        MacroFleshRebuilderMp.EscapeStarted = true;
        FindTheBugInator.Mark("escape-ending");
        return true;
    }

    static IEnumerator Done() {
        yield break;
    }
}

// carried body is the one on the back. carrier is who is holding them.
public static class EndingCarry {
    public static bool Blocked(NetBody carried, NetBody carrier) {
        if (carried == null || carrier == null) {
            return false;
        }
        if (ConcurrentEndings.ScientistDead && IsChippedPlayer(carried)) {
            return true;
        }
        return MacroFleshRebuilderMp.BlocksCarry(carrier);
    }

    public static void DropIllegal() {
        List<NetBody> drop = null;
        foreach (NetBody nb in NetBody.all_instances) {
            if (nb == null || nb.piggybacking_on == null) {
                continue;
            }
            if (!Blocked(nb, nb.piggybacking_on)) {
                continue;
            }
            if (drop == null) {
                drop = new List<NetBody>();
            }
            drop.Add(nb);
        }
        if (drop == null) {
            return;
        }
        for (int i = 0; i < drop.Count; i++) {
            if (drop[i] != null && drop[i].piggybacking_on != null) {
                drop[i].StopPiggyback();
            }
        }
    }

    public static void DropFor(NetBody carrier) {
        if (carrier == null) {
            return;
        }
        NetBody carried = carrier.carrying_person;
        if (carried != null && carried.piggybacking_on != null) {
            carried.StopPiggyback();
        }
    }

    static bool IsChippedPlayer(NetBody nb) {
        if (nb == null || nb.plr == null) {
            return false;
        }
        return !nb.plr.IsUnchipped();
    }
}

[HarmonyPatch(typeof(MusicManager), "PlaySong")]
static class MusicManager_PlaySong_EndingAudible {
    static void Postfix() {
        if (MusicManager.main == null || WorldGeneration.world == null) {
            return;
        }
        if (WorldGeneration.world.biomeDepth >= 10) {
            MusicManager.main.cutsceneVolMult = 1f;
        }
    }
}

#endif