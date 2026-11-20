using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using LiteNetLib.Utils;
using Newtonsoft.Json.Linq;
using Steamworks;
using Together;

static class Checks {
    static int passed;
    static readonly string Work = Path.Combine(Path.GetTempPath(), "TogetherHostTransferChecks-" + Guid.NewGuid().ToString("N"));
    static readonly BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static readonly LobbyHostTransfer Controller = new();
    static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action, string name) {
        try { action(); } catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is InvalidOperationException || ex is ArgumentException) { Check(true, name); return; }
        throw new Exception("Expected rejection: " + name);
    }
    static object Field(string n) => typeof(LobbyHostTransfer).GetField(n, Hidden)!.GetValue(null);
    static void Set(string n, object value) => typeof(LobbyHostTransfer).GetField(n, Hidden)!.SetValue(null, value);
    static bool InPhase(string name) => Field("phase").ToString() == name;
    static void SetPhase(string name) => Set("phase", Enum.Parse(typeof(LobbyHostTransfer).GetNestedType("Phase", Hidden)!, name));
    static void Call(string n) => typeof(LobbyHostTransfer).GetMethod(n, Hidden)!.Invoke(n == "Update" || n == "Awake" ? Controller : null, null);
    static string Token => (string)Field("id");
    static ScavPlayer Player(ulong id) => ScavPlayer.AllPlayers.Single(p => p.SteamId == id);
    static void RequestAtExit() => LobbyHostTransfer.Request(Player(2), LobbyHostTransfer.TransferMode.NextLayer);
    static void Reset(bool host = true) {
        Call("Reset"); Set("pendingTransferredLoad", false);
        Set("holdLobbyAutoStart", false);
        UnityEngine.MonoBehaviour.Work.Clear();
        KSteam.Local = host ? 1UL : 2UL; KSteam.LobbyId = new(100); KSteam.CURRENT_LOBBY.ownerID = new(1);
        KSteam.IS_IN_LOBBY = true;
        SteamMatchmaking.Owner = 1; SteamMatchmaking.AllowOwnerChange = true; SteamMatchmaking.Data.Clear();
        WorldGeneration.world = new(); Util.Menu = false; SavesystemPatch.PreserveSave = false;
        Net.IsRunning = Net.IsRunningSteam = true; Net.IsHost = host; Net.Outbox.Clear();
        UnityEngine.Time.realtimeSinceStartupAsDouble = 0; Multiplayer.Logs.Clear();
        ScavPlayer.AllPlayers = new() {
            new() { SteamId = 1, playerId = 0, playerName = "host", IsLocal = host },
            new() { SteamId = 2, playerId = 1, playerName = "meow", IsLocal = !host },
            new() { SteamId = 3, playerId = 2, playerName = "third" }
        };
        ScavPlayer.LocalPlayer = Player(KSteam.Local);
        TransferTransport.Starts = TransferTransport.Detaches = 0;
        HostTransferUI.SelectingAtExit = false; PreRunScript.instance = new();
    }
    static NetDataWriter Packet(byte kind, string token = null) {
        var w = new NetDataWriter(); w.Put(2); w.Put(kind); w.Put(token ?? Token ?? Guid.NewGuid().ToString("N")); return w;
    }
    static void Answer(ulong sender, bool yes = true) {
        var w = Packet(1); w.Put(yes); Multiplayer.Server(Player(sender), new NetDataReader(w.CopyData()));
    }
    static string OfferAndPrepare(LobbyHostTransfer.TransferMode mode = LobbyHostTransfer.TransferMode.NextLayer) {
        string token = Guid.NewGuid().ToString("N");
        var offer = Packet(0, token); offer.Put(100UL); offer.Put(1UL); offer.Put(2UL); offer.Put("meow");
        offer.Put((byte)mode);
        Multiplayer.Client(new NetDataReader(offer.CopyData()));
        LobbyHostTransfer.AnswerOffer(token, true);
        var prepare = Packet(2, token); prepare.Put(100UL); prepare.Put(1UL); prepare.Put(2UL); prepare.Put("meow");
        prepare.Put((byte)mode);
        prepare.Put("password"); prepare.Put("settings"); prepare.Put(4); prepare.Put(3); prepare.Put(1UL); prepare.Put(2UL); prepare.Put(3UL);
        Multiplayer.Client(new NetDataReader(prepare.CopyData())); return token;
    }
    static byte[] Archive(params (string path, byte[] bytes)[] files) {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var f in files) using (var s = zip.CreateEntry(f.path).Open()) s.Write(f.bytes);
        return output.ToArray();
    }
    static void StepWork() {
        foreach (var work in UnityEngine.MonoBehaviour.Work.ToArray())
            if (!work.MoveNext()) UnityEngine.MonoBehaviour.Work.Remove(work);
    }
    static IEnumerator ContinueLayer() {
        LobbyHostTransfer.ArmBoundary(WorldGeneration.world, false);
        object[] args = { WorldGeneration.world, WorldGeneration.world.Clear() };
        typeof(LobbyHostTransfer).GetNestedType("AtClear", Hidden)!.GetMethod("Postfix", Hidden)!.Invoke(null, args);
        return (IEnumerator)args[1];
    }
    static void Ready(ulong sender, byte kind) {
        var w = Packet(kind); w.Put(true); Multiplayer.Server(Player(sender), new NetDataReader(w.CopyData()));
    }
    static void Deliver(byte[] bytes) {
        var begin = Packet(3); begin.Put(bytes.Length); begin.Put(TransferArchive.Hash(bytes)); Multiplayer.Client(new NetDataReader(begin.CopyData()));
        for (int offset = 0; offset < bytes.Length; offset += 4096) {
            byte[] part = bytes.Skip(offset).Take(4096).ToArray();
            var chunk = Packet(4); chunk.Put(offset); chunk.Put(part.Length); chunk.Put(part);
            Multiplayer.Client(new NetDataReader(chunk.CopyData()));
        }
    }
    static string Stage(byte[] bytes) => TransferArchive.Stage(bytes, Path.Combine(Work, Guid.NewGuid().ToString("N")), Guid.NewGuid().ToString("N"));
    static void Save(string root, ulong id, int depth) {
        Directory.CreateDirectory(Path.Combine(root, "STEAM_" + id));
        File.WriteAllBytes(Path.Combine(root, "STEAM_" + id, "save.sv"), SaveSystem.Zip("{\"biome\":" + depth + ",\"owner\":" + id + ",\"inventory\":[\"food\"],\"ggSpecies\":\"custom\",\"craftedBefore\":[1]}"));
    }
    static int Main() {
        Directory.CreateDirectory(Work);
        try {
            string source = Path.Combine(Work, "source"); Directory.CreateDirectory(source);
            foreach (ulong i in new ulong[] { 1, 2, 3, 4 }) Save(source, i, 5);
            File.WriteAllText(Path.Combine(source, "mp_rules.json"), "{\"LEVEL\":true,\"RULES\":{\"LateJoinAllowed\":true},\"MODIFIER\":2,\"PLRPOS\":{\"old\":[2,3]}}");
            File.WriteAllText(Path.Combine(source, "world_tiles.bin"), "old map");
            File.WriteAllText(Path.Combine(source, "another-slot.sv"), "other slot");
            var members = new ulong[] { 1, 2, 3 };
            byte[] built = TransferArchive.Build(source, 2, 4, members); string stage = Stage(built);
            TransferArchive.ValidateCheckpoint(stage, 2, 4, members);
            Check(TransferArchive.ReadSave(Path.Combine(stage, "save.sv")).Value<ulong>("owner") == 2, "recipient owns root save");
            Check(members.All(i => TransferArchive.ReadSave(Path.Combine(stage, "STEAM_" + i, "save.sv")).Value<int>("biome") == 4), "already advanced layer retained without skipping");
            Check(TransferArchive.ReadSave(Path.Combine(stage, "STEAM_2", "save.sv"))["inventory"]![0]!.Value<string>() == "food", "inventory preserved");
            Check(TransferArchive.ReadSave(Path.Combine(stage, "save.sv")).Value<string>("ggSpecies") == "custom", "custom species preserved");
            Check(TransferArchive.ReadSave(Path.Combine(stage, "save.sv"))["craftedBefore"]![0]!.Value<int>() == 1, "craft history preserved");
            Check(File.Exists(Path.Combine(stage, "STEAM_4", "save.sv")), "disconnected player checkpoint retained");
            Check(!File.Exists(Path.Combine(stage, "world_tiles.bin")) && !File.Exists(Path.Combine(stage, "another-slot.sv")), "old map and extra slots omitted");
            Check(TransferArchive.ReadSave(Path.Combine(source, "STEAM_1", "save.sv")).Value<int>("biome") == 5, "building archive leaves source untouched");
            Check(TransferArchive.Hash(built).Length == 64 && TransferArchive.Hash(built) == TransferArchive.Hash(built.ToArray()), "checkpoint SHA256 stable");
            Reject(() => TransferArchive.Build(source, 9, 4, members), "missing recipient rejected");
            Reject(() => TransferArchive.Build(source, 2, 0, members), "invalid depth rejected");
            Reject(() => TransferArchive.Build(source, 2, 4, new ulong[] { 1, 2, 7 }), "missing member checkpoint rejected");
            Reject(() => TransferArchive.ValidateCheckpoint(stage, 1, 4, members), "wrong root character rejected");
            Reject(() => TransferArchive.ValidateCheckpoint(stage, 2, 3, members), "wrong saved depth rejected");
            foreach (string path in new[] { "../save.sv", "STEAM_1/../../save.sv", "C:/save.sv", "STEAM_+1/save.sv", "STEAM_ 1/save.sv", "STEAM_01/save.sv", "STEAM_0/save.sv", "STEAM_1\\save.sv" })
                Reject(() => Stage(Archive((path, new byte[] { 1 }))), "unsafe path " + path);
            Reject(() => Stage(Archive(("save.sv", new byte[] { 1 }), ("save.sv", new byte[] { 2 }))), "duplicate archive path rejected");
            Reject(() => Stage(Archive(("save.sv", new byte[] { 1 }))), "incomplete checkpoint rejected");
            Reject(() => TransferArchive.Stage(built, source, "../bad"), "invalid transfer ID rejected");
            string bomb = Path.Combine(Work, "bomb.sv"); File.WriteAllBytes(bomb, SaveSystem.Zip(new string('a', TransferArchive.MaxSaveJson + 1)));
            Reject(() => TransferArchive.ReadSave(bomb), "nested gzip expansion bounded");
            string install = Path.Combine(Work, "recipient"), token = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(install); File.WriteAllText(Path.Combine(install, "original.txt"), "keep");
            string staged = TransferArchive.Stage(built, install, token);
            string recovery = Path.Combine(install + "_host_transfer_backups", token);
            Check(staged == Path.Combine(recovery, "staged-save"), "staging is grouped by transfer outside the active save");
            Check(File.ReadAllText(Path.Combine(install, "original.txt")) == "keep", "staging leaves existing saves intact");
            TransferArchive.Install(staged, install, token);
            Check(File.ReadAllText(Path.Combine(recovery, "previous-save", "original.txt")) == "keep", "install retains recipient save in transfer backup folder");
            Check(!Directory.Exists(staged), "installed checkpoint leaves no staging directory behind");
            string missingToken = Guid.NewGuid().ToString("N");
            Reject(() => TransferArchive.Install(Path.Combine(install + "_host_transfer_backups", missingToken, "staged-save"), install, missingToken), "failed installation reports error");
            Check(File.Exists(Path.Combine(install, "save.sv")), "failed installation restores prior save folder");
            Reject(() => TransferArchive.Install(install + ".together-stage-" + token, install, token), "installation still rejects an unexpected staging path");
            string secondToken = Guid.NewGuid().ToString("N");
            TransferArchive.Install(TransferArchive.Stage(built, install, secondToken), install, secondToken);
            Check(File.Exists(Path.Combine(recovery, "previous-save", "original.txt"))
                && File.Exists(Path.Combine(install + "_host_transfer_backups", secondToken, "previous-save", "save.sv")), "successive transfers retain separate backups");
            TransferArchive.NormalizeSource(source, 1, 4, members, Guid.NewGuid().ToString("N"));
            Check(TransferArchive.ReadSave(Path.Combine(source, "save.sv")).Value<ulong>("owner") == 1, "original host recovery remains its own character");
            Check(TransferArchive.ReadSave(Path.Combine(source, "save.sv")).Value<int>("biome") == 4, "original host recovery has same next depth");

            Call("Awake"); Reset(); RequestAtExit();
            Check(Token != null && Net.Outbox.Count == 1, "layer-exit player selection sends hosting offer");
            Answer(3); Check(InPhase("Offered"), "non-recipient cannot accept");
            Answer(2); Check(InPhase("Accepted"), "recipient accepts queued transfer");
            LobbyHostTransfer.ArmBoundary(WorldGeneration.world, false);
            Check((bool)Field("boundaryArmed"), "common continuation arms accepted transfer");
            LobbyHostTransfer.Cancel();
            Check(Token == null && InPhase("Idle") && !LobbyHostTransfer.Busy, "cancel clears queue before commit");
            Reset(false); Reject(RequestAtExit, "client cannot nominate host");
            Reset(); RequestAtExit();
            LobbyHostTransfer.ArmBoundary(WorldGeneration.world, false); Check(Token == null, "unaccepted continuation cancels safely");
            foreach (int mode in new[] { 0, 1, 2 }) {
                Reset(); RequestAtExit(); Answer(2);
                WorldGeneration.world.doPod = mode == 1; WorldGeneration.world.biomeDepth = mode == 2 ? 10 : 3;
                LobbyHostTransfer.ArmBoundary(WorldGeneration.world, mode == 0);
                Check(Token == null, "unsupported descent cancels mode " + mode);
            }
            Reset(); RequestAtExit();
            UnityEngine.Time.realtimeSinceStartupAsDouble = 61; Call("Update"); Check(Token == null, "unanswered offer expires");
            Reset(false); OfferAndPrepare(); Util.Menu = true; Call("Update");
            Check(InPhase("Preparing"), "recipient cannot acknowledge readiness without checkpoint");
            Check(!LobbyHostTransfer.AllowRun(true) && !LobbyHostTransfer.AllowRun(false), "start and load blocked during preparation");
            var badBegin = Packet(3); badBegin.Put(TransferArchive.MaxBytes + 1); badBegin.Put(new string('A', 64));
            Multiplayer.Client(new NetDataReader(badBegin.CopyData())); Check(Token == null, "oversized wire checkpoint rejected");
            Reset(false); OfferAndPrepare();
            var begin = Packet(3); begin.Put(20); begin.Put(new string('A', 64)); Multiplayer.Client(new NetDataReader(begin.CopyData()));
            var badChunk = Packet(4); badChunk.Put(1); badChunk.Put(int.MaxValue);
            Multiplayer.Client(new NetDataReader(badChunk.CopyData())); Check(Token == null, "invalid chunk rejected before allocation");
            Reset(false); token = OfferAndPrepare(); Util.Menu = true;
            Set("stage", stage); Call("Update"); Check(InPhase("Ready"), "staged recipient acknowledges lobby readiness");
            Check(!LobbyHostTransfer.HandlePlannedDisconnect(1), "unexpected host disconnect not suppressed before commit");
            Multiplayer.Client(new NetDataReader(Packet(9).CopyData()));
            Check(LobbyHostTransfer.HandlePlannedDisconnect(1) && InPhase("CommitApproved"), "acknowledged socket close waits for delayed Steam owner update");
            SteamMatchmaking.Owner = 2;
            Check(!LobbyHostTransfer.HandlePlannedDisconnect(3), "unrelated disconnect not suppressed");
            Check(LobbyHostTransfer.HandlePlannedDisconnect(1) && InPhase("Switching"), "planned old-host close starts handoff after commit");
            UnityEngine.Time.realtimeSinceStartupAsDouble = 91; Call("Update");
            Check(!Net.IsRunning && Token == null, "post-commit timeout leaves failed session without claiming completion");
            Check(!Multiplayer.Logs.Any(s => s.StartsWith("[LobbyTransfer] Host transfer complete")), "failed handoff never reports success");

            Reset(false); token = OfferAndPrepare(); Util.Menu = true;
            SavesystemPatch.mpsavefolder = Path.Combine(Work, "wire-recipient");
            Directory.CreateDirectory(SavesystemPatch.mpsavefolder);
            File.WriteAllText(Path.Combine(SavesystemPatch.mpsavefolder, "keep.txt"), "prior run");
            begin = Packet(3); begin.Put(built.Length); begin.Put(TransferArchive.Hash(built)); Multiplayer.Client(new NetDataReader(begin.CopyData()));
            for (int offset = 0; offset < built.Length; offset += 50) {
                byte[] part = built.Skip(offset).Take(50).ToArray();
                var chunk = Packet(4); chunk.Put(offset); chunk.Put(part.Length); chunk.Put(part);
                Multiplayer.Client(new NetDataReader(chunk.CopyData()));
            }
            Check(Field("stage") != null, "valid wire chunks hash and stage checkpoint");
            Check(File.Exists(Path.Combine(SavesystemPatch.mpsavefolder, "keep.txt")), "wire delivery preserves existing recipient run before commit");
            Call("Update"); Multiplayer.Client(new NetDataReader(Packet(9).CopyData())); SteamMatchmaking.Owner = 2; Call("Update");
            Check(TransferTransport.Starts == 0, "ownership observed before new host transport starts");
            StepWork(); StepWork();
            Check(TransferTransport.Starts == 1 && Net.IsHost && SteamMatchmaking.Data["together_transfer_ready"] == token, "new host opens transport then publishes readiness");
            Check(TransferArchive.ReadSave(Path.Combine(SavesystemPatch.mpsavefolder, "save.sv")).Value<ulong>("owner") == 2, "new host installs delivered character checkpoint");
            Call("Update"); Check(Token != null, "new host waits for all arrival acknowledgements");
            Ready(1, 7); Call("Update"); Check(Token != null, "one missing peer prevents completion");
            Ready(3, 7); Call("Update");
            Check(Token == null && (bool)Field("pendingTransferredLoad"), "all peers confirmed completes handoff and retains next-load slot marker");
            Check(Multiplayer.Logs.Any(s => s.Contains("Host transfer complete. You are hosting")), "success reported only after group arrival");
            Check(InPhase("Resuming") && LobbyHostTransfer.Busy && PreRunScript.instance.Loads == 0, "automatic resume scheduled after completion acknowledgement");
            Call("Update");
            Check(InPhase("Idle") && PreRunScript.instance.Loads == 1 && !PreRunScript.instance.WasBusy, "next-layer handoff resumes through load path with start gate released");
            Call("Update"); Check(PreRunScript.instance.Loads == 1, "automatic resume happens once");
            Util.Menu = false; Call("Update");
            Check((bool)Field("pendingTransferredLoad"), "transferred load policy survives early world scene initialization");
            WorldMP.Finish(); Check(!(bool)Field("pendingTransferredLoad"), "transferred load policy clears after actual world-generation finish");
            Reset(); RequestAtExit(); Answer(2);
            SetPhase("Preparing"); Set("members", new HashSet<ulong>(members));
            Util.Menu = true; Ready(2, 5); Ready(3, 5); Call("Update");
            Check(SteamMatchmaking.Owner == 1, "all peers must acknowledge planned disconnect before ownership changes");
            Ready(2, 10); Call("Update"); Check(SteamMatchmaking.Owner == 1, "missing commit acknowledgement prevents ownership change");
            Ready(3, 10); Call("Update");
            Check(SteamMatchmaking.Owner == 2, "original host commits only after every peer prepared");
            StepWork(); StepWork();
            Check(TransferTransport.Detaches == 1 && TransferTransport.Starts == 0, "former host waits for new listener after detaching");
            SteamMatchmaking.Data["together_transfer_ready"] = "other-session"; StepWork();
            Check(TransferTransport.Starts == 0, "stale listener token cannot reconnect former host");
            SteamMatchmaking.Data["together_transfer_ready"] = Token; StepWork();
            Check(TransferTransport.Starts == 1 && Net.IsClient, "former host reconnects as client when listener ready");

            // Main-menu saves keep native stored depth and mid-level world data.
            string saved = Path.Combine(Work, "lobby-source"); Directory.CreateDirectory(saved);
            foreach (ulong p in members) Save(saved, p, 6);
            File.Copy(Path.Combine(saved, "STEAM_1", "save.sv"), Path.Combine(saved, "save.sv"));
            File.WriteAllText(Path.Combine(saved, "mp_rules.json"), "{\"LEVEL\":true,\"RULES\":{},\"MODIFIER\":3,\"PLRPOS\":{\"STEAM_1\":[3,4]}}");
            File.WriteAllBytes(Path.Combine(saved, "world_tiles.bin"), new byte[] { 2, 3, 4 });
            byte[] savedBundle = TransferArchive.BuildSaved(saved, 2);
            string savedStage = Stage(savedBundle); TransferArchive.ValidateSaved(savedStage, 2);
            Check(TransferArchive.ReadSave(Path.Combine(savedStage, "save.sv")).Value<int>("biome") == 6, "lobby save transfer does not advance or normalize stored depth");
            Check(JObject.Parse(File.ReadAllText(Path.Combine(savedStage, "mp_rules.json"))).Value<bool>("LEVEL"), "lobby transfer retains mid-layer save flag");
            Check(File.ReadAllBytes(Path.Combine(savedStage, "world_tiles.bin")).SequenceEqual(new byte[] { 2, 3, 4 }), "lobby transfer retains saved terrain file");
            Check(JObject.Parse(File.ReadAllText(Path.Combine(savedStage, "mp_rules.json"))).Value<int>("MODIFIER") == 3, "lobby save retains modifier metadata");
            Check(!TransferArchive.NeedsFreshHost(savedStage, 2), "existing recipient character used when present");
            string freshStage = Stage(TransferArchive.BuildSaved(saved, 9)); TransferArchive.ValidateSaved(freshStage, 9);
            Check(TransferArchive.NeedsFreshHost(freshStage, 9), "new recipient explicitly marked to keep a fresh character");
            Check(!File.Exists(Path.Combine(freshStage, "STEAM_9", "save.sv")), "former host body never copied into a new recipient player folder");
            Check(File.ReadAllBytes(Path.Combine(saved, "save.sv")).SequenceEqual(File.ReadAllBytes(Path.Combine(saved, "STEAM_1", "save.sv"))), "building lobby archive leaves original host save intact");

            Reset(false); Util.Menu = true; OfferAndPrepare(LobbyHostTransfer.TransferMode.LobbyOnly);
            Check(HostTransferUI.OfferText.Contains("will not be replaced"), "hosting-only recipient told saves are retained");
            SavesystemPatch.mpsavefolder = Path.Combine(Work, "hosting-only-target"); Directory.CreateDirectory(SavesystemPatch.mpsavefolder);
            File.WriteAllText(Path.Combine(SavesystemPatch.mpsavefolder, "prior.txt"), "preserve");
            Call("Update"); Check(InPhase("Ready"), "hosting-only recipient needs no checkpoint download");
            Multiplayer.Client(new NetDataReader(Packet(9).CopyData())); SteamMatchmaking.Owner = 2; Call("Update"); StepWork(); StepWork();
            Check(TransferTransport.Starts == 1 && !TransferTransport.InstalledSave, "hosting-only new host starts without waiting for its own listener token");
            Ready(1, 7); Ready(3, 7); Call("Update"); Call("Update");
            Check(PreRunScript.instance.Loads == 0 && !LobbyHostTransfer.AwaitingTransferredLoad, "hosting-only transfer stays in lobby without pending load");
            Check(LobbyHostTransfer.HoldLobbyAutoStart, "hosting-only lobby transfer holds automatic new-run start");
            Check(File.ReadAllText(Path.Combine(SavesystemPatch.mpsavefolder, "prior.txt")) == "preserve", "hosting-only transfer never replaces recipient files");

            Reset(false); Util.Menu = true; OfferAndPrepare(LobbyHostTransfer.TransferMode.LobbyWithSave);
            Check(HostTransferUI.OfferText.Contains("replace your active multiplayer expedition") && HostTransferUI.OfferText.Contains("backed up"), "save replacement warning shown before recipient acceptance");
            Check(HostTransferUI.OfferText.Contains("QoL"), "warning explains separate QoL slots without promising an exemption");
            SavesystemPatch.mpsavefolder = Path.Combine(Work, "saved-lobby-target"); Directory.CreateDirectory(SavesystemPatch.mpsavefolder);
            string qoslot = SavesystemPatch.mpsavefolder + "-qol-slot.sv"; File.WriteAllText(qoslot, "separate slot");
            Deliver(savedBundle); Check(Field("stage") != null, "lobby saved expedition validates without imposing next-layer rules");
            Call("Update"); Multiplayer.Client(new NetDataReader(Packet(9).CopyData())); SteamMatchmaking.Owner = 2; Call("Update"); StepWork(); StepWork();
            Ready(1, 7); Ready(3, 7); Call("Update"); Call("Update");
            Check(PreRunScript.instance.Loads == 0 && LobbyHostTransfer.AwaitingTransferredLoad, "saved lobby transfer awaits manual Resume Expedition");
            Check(LobbyHostTransfer.HoldLobbyAutoStart && LobbyHostTransfer.AllowRun(true) && !LobbyHostTransfer.HoldLobbyAutoStart, "explicit lobby resume releases autostart hold");
            Check(File.ReadAllText(qoslot) == "separate slot", "separate save-slot file outside active multiplayer folder preserved");

            Reset(); Util.Menu = true; SavesystemPatch.mpsavefolder = saved;
            LobbyHostTransfer.Request(Player(2), LobbyHostTransfer.TransferMode.LobbyOnly); Answer(2);
            Check(LobbyHostTransfer.Busy && UnityEngine.MonoBehaviour.Work.Count == 1, "lobby acceptance begins preparation without a layer continuation");
            Reset(); RequestAtExit(); Answer(2); Call("Update"); Call("Update");
            Check(WorldGeneration.world.ContinueCalls == 1, "layer-exit picker acceptance requests native continuation once");
            Reset(); HostTransferUI.SelectingAtExit = true; Check(LobbyHostTransfer.PauseAutoContinue, "layer player picker pauses auto continuation");
            HostTransferUI.SelectingAtExit = false; RequestAtExit();
            Check(LobbyHostTransfer.PauseAutoContinue, "automatic continuation waits for recipient consent");
            Answer(2); Check(!LobbyHostTransfer.PauseAutoContinue, "automatic continuation allowed after acceptance");

            // Exercise the actual continuation wrapper and checkpoint capture,
            // including failure before returning the group to the menu.
            Reset();
            string capture = Path.Combine(Work, "capture");
            TransferArchive.CopyTree(source, capture); SavesystemPatch.mpsavefolder = capture;
            foreach (ulong p in members) Save(capture, p, 3);
            SaveSystem.SaveAction = () => {
                foreach (ulong p in members) {
                    string path = Path.Combine(capture, "STEAM_" + p, "save.sv");
                    DateTime stamp = File.GetLastWriteTimeUtc(path);
                    Save(capture, p, 4); File.SetLastWriteTimeUtc(path, stamp.AddMinutes(1));
                }
            };
            RequestAtExit(); Answer(2);
            WorldGeneration.world.biomeDepth = 4;
            var continuation = ContinueLayer();
            Check(InPhase("Preparing") && LobbyHostTransfer.Busy, "continuation enters preparation after native depth advancement");
            continuation.MoveNext(); StepWork();
            Check(Util.Menu && Token != null, "fresh checkpoint capture returns the group to the lobby");
            recovery = Path.Combine(capture + "_host_transfer_backups", Token);
            Check(TransferArchive.ReadSave(Path.Combine(recovery, "source-before-checkpoint", "STEAM_2", "save.sv")).Value<int>("biome") == 3, "source backup precedes checkpoint rewrite");
            byte[] captured = File.ReadAllBytes(Path.Combine(recovery, "checkpoint.zip"));
            TransferArchive.ValidateCheckpoint(Stage(captured), 2, 4, members);
            Check(true, "captured transfer archive contains every player's advanced checkpoint");
            Check(Directory.Exists(Path.Combine(recovery, "previous-save")) && !Directory.Exists(Path.Combine(capture, "source-before-checkpoint"))
                && Directory.GetFileSystemEntries(Work).All(path => !Path.GetFileName(path).Contains(".together-")), "transfer backups and archives stay out of the save parent folder");
            LobbyHostTransfer.Cancel();
            Check(!continuation.MoveNext() && WorldGeneration.world.ClearCalls == 0, "return to lobby releases wrapper without regenerating the departed world");
            Reset(); SaveSystem.SaveAction = () => {};
            RequestAtExit(); Answer(2);
            WorldGeneration.world.biomeDepth = 4; continuation = ContinueLayer();
            continuation.MoveNext(); StepWork();
            Check(Token == null && InPhase("Idle") && !Util.Menu, "stale checkpoint stops handoff before returning players to the lobby");
            Check(!continuation.MoveNext() && WorldGeneration.world.ClearCalls == 1, "failed capture falls back to the original continuation");

            var rejected = new List<string>();
            var defaults = new Dictionary<string, object> { ["unchipped"] = false, ["lootmultiplier"] = 1f, ["ambientlight"] = 0 };
            var decoded = RunSettingsCodec.Decode(JArray.Parse("[{\"Item1\":\"unchipped\",\"Item2\":true},{\"Item1\":\"lootmultiplier\",\"Item2\":2.5},{\"Item1\":\"ambientlight\",\"Item2\":2},{\"Item1\":\"legacy_fullgame_key\",\"Item2\":false},{\"Item1\":\"extra\",\"Item2\":{}}]"), defaults, rejected.Add);
            Check(decoded["unchipped"] is bool b && b, "run settings retain Boolean type");
            Check(decoded["lootmultiplier"] is float f && f == 2.5f && decoded["ambientlight"] is int n && n == 2, "preset types retained for float and dropdown settings");
            Check(decoded.ContainsKey("legacy_fullgame_key") && rejected.Count == 1 && decoded.Count == 4, "unregistered fullgame scalar does not discard later settings");
            Console.WriteLine($"HOST_TRANSFER_CHECKS passed={passed} failed=0; fixtures={Work}"); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); Console.Error.WriteLine("Fixtures preserved: " + Work); return 1; }
    }
}
