using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using Newtonsoft.Json.Linq;
using System.IO.Compression;
using System.Text;

[BepInPlugin("together.hosttransfer.runtimechecks", "Host Transfer Runtime Checks", "1.0.0")]
[BepInDependency("CasualtiesMP")]
public sealed class HostTransferRuntimeChecks : BaseUnityPlugin {
    int passed, failed;
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    void Check(bool condition, string name) {
        if (condition) { passed++; Logger.LogInfo("PASS " + name); }
        else { failed++; Logger.LogError("FAIL " + name); }
    }
    IEnumerator Start() {
        yield return new WaitForSecondsRealtime(8);
        try { Run(); } catch (Exception e) { failed++; Logger.LogError(e); }
        // Inspect the actual layer-exit canvas without generating or saving a run.
        var layerChecks = LayerUI();
        while (true) {
            object next;
            try { if (!layerChecks.MoveNext()) break; next = layerChecks.Current; }
            catch (Exception e) { failed++; Logger.LogError(e); break; }
            yield return next;
        }
        Logger.LogInfo($"HOST_TRANSFER_RUNTIME_COMPLETE passed={passed} failed={failed}");
        File.WriteAllText(Path.Combine(Paths.GameRootPath, "runtime-checks-result.txt"), $"passed={passed} failed={failed}");
        yield return new WaitForSecondsRealtime(3);
        Application.Quit();
    }
    void Run() {
        Assembly mp = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "CasualtiesMP");
        Assembly game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
        Type controller = mp.GetType("Together.LobbyHostTransfer", true);
        var plugin = BepInEx.Bootstrap.Chainloader.PluginInfos["CasualtiesMP"].Instance;
        Check(plugin.GetComponent(controller) != null, "persistent host-transfer component initialized");
        Check(plugin.GetComponent(mp.GetType("Together.HostTransferUI", true)) != null, "persistent transfer UI component initialized");
        Type console = game.GetType("ConsoleScript", true);
        object command = console.GetMethod("SearchExact", All).Invoke(null, new object[] { "transferhost" });
        Check(command == null, "transferhost absent from real console");
        Type con = mp.GetType("Together.Con", true);
        var local = (IEnumerable)con.GetField("localonly_commands", All).GetValue(null);
        Check(!local.Cast<object>().Any(v => (string)v == "transferhost"), "transferhost absent from local-only command list");
        foreach (string method in new[] { "Clear", "RegenerateWorld" }) {
            MethodInfo target = AccessTools.Method(game.GetType("WorldGeneration", true), method);
            var info = Harmony.GetPatchInfo(target);
            Check(info != null && info.Prefixes.Concat(info.Postfixes).Any(p => p.PatchMethod.DeclaringType == mp.GetType("Together.RegenerateWorldPatch")
                || p.PatchMethod.DeclaringType.DeclaringType == controller), "native " + method + " handoff hook installed");
        }
        var loadInfo = Harmony.GetPatchInfo(AccessTools.Method(game.GetType("SaveSystem", true), "TryLoadGame"));
        Check(loadInfo != null && loadInfo.Finalizers.Any(p => p.PatchMethod.DeclaringType.DeclaringType == controller), "QoL slot restoration finalizer installed");
        var hasInfo = Harmony.GetPatchInfo(AccessTools.Method(game.GetType("SaveSystem", true), "HasSave"));
        Check(hasInfo != null && hasInfo.Finalizers.Any(p => p.PatchMethod.DeclaringType.DeclaringType == controller), "transferred save visibility hook installed");
        Type multi = mp.GetType("Together.Multiplayer", true);
        foreach (string name in new[] { "_ClientReceivers", "_ServerReceivers" }) {
            var dict = (IDictionary)multi.GetField(name, All).GetValue(null);
            Check(dict.Values.Cast<Delegate>().Any(d => d.Method.DeclaringType == controller), name + " contains handoff protocol receiver");
        }
        Type archive = mp.GetType("Together.TransferArchive", true);
        string root = Path.Combine(Paths.GameRootPath, "host-transfer-native-fixtures");
        Directory.CreateDirectory(root);
        Type saves = game.GetType("SaveSystem", true);
        foreach (ulong id in new ulong[] { 1, 2 }) {
            string dir = Path.Combine(root, "STEAM_" + id); Directory.CreateDirectory(dir);
            byte[] bytes = (byte[])saves.GetMethod("Zip", All).Invoke(null, new object[] { "{\"biome\":5,\"owner\":" + id + "}" });
            File.WriteAllBytes(Path.Combine(dir, "save.sv"), bytes);
        }
        File.WriteAllText(Path.Combine(root, "mp_rules.json"), "{\"LEVEL\":true,\"RULES\":{},\"MODIFIER\":2,\"PLRPOS\":{}}");
        byte[] bundle = (byte[])archive.GetMethod("Build", All).Invoke(null, new object[] { root, 2UL, 4, new ulong[] { 1, 2 } });
        string stage = (string)archive.GetMethod("Stage", All).Invoke(null, new object[] { bundle, root + "-recipient", Guid.NewGuid().ToString("N") });
        archive.GetMethod("ValidateCheckpoint", All).Invoke(null, new object[] { stage, 2UL, 4, new ulong[] { 1, 2 } });
        Check(true, "checkpoint round-trip reads actual game gzip format");
        Check(!File.Exists(Path.Combine(root + "-recipient", "save.sv")), "native staging does not install recipient save before commit");
        // Reproduce the real playtest run-settings packet, including preset-only
        // keys that the vanilla RunSettings.GetSetting lookup cannot find.
        string settingsFixture = Path.Combine(Paths.GameRootPath, "reported-run-settings.txt");
        if (File.Exists(settingsFixture)) {
            var worldMP = mp.GetType("Together.WorldMP", true);
            var decoded = (IDictionary)worldMP.GetMethod("ReadRunSettings", All).Invoke(null, new object[] { File.ReadAllText(settingsFixture).Trim() });
            Check(decoded.Contains("immortalsnail") && decoded.Contains("lootmultiplier"), "reported fullgame run-settings packet decodes all scalar keys");
            using (var packed = new MemoryStream(Convert.FromBase64String(File.ReadAllText(settingsFixture).Trim())))
            using (var gzip = new GZipStream(packed, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, Encoding.UTF8)) {
                var entries = JArray.Parse(reader.ReadToEnd());
                entries.OfType<JObject>().First(p => p.Value<string>("Item1") == "lootmultiplier")["Item2"] = 2.75f;
                entries.Add(new JObject { ["Item1"] = "TestExtraRunSetting", ["Item2"] = true });
                using (var output = new MemoryStream()) {
                    using (var zip = new GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) {
                        byte[] bytes = Encoding.UTF8.GetBytes(entries.ToString()); zip.Write(bytes, 0, bytes.Length);
                    }
                    var changed = (IDictionary)worldMP.GetMethod("ReadRunSettings", All).Invoke(null, new object[] { Convert.ToBase64String(output.ToArray()) });
                    Check(changed["lootmultiplier"] is float number && number == 2.75f && changed.Contains("TestExtraRunSetting"), "run-settings decoder retains nondefault values past unregistered keys");
                }
            }
        }
        var qol = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("QoL_Unknown.SaveManager")).FirstOrDefault(t => t != null);
        var slot = qol?.GetField("CurrentSaveFile", All);
        Check(slot != null, "installed QoL slot API found");
        if (slot != null) {
            object previous = slot.GetValue(null);
            try {
                slot.SetValue(null, "test-slot.sv");
                using ((IDisposable)mp.GetType("Together.TransferTransport").GetMethod("SelectQoLSave", All).Invoke(null, null))
                    Check((string)slot.GetValue(null) == "save.sv", "transferred save selection scoped to operation");
                Check((string)slot.GetValue(null) == "test-slot.sv", "prior QoL slot selection restored");
            } finally { slot.SetValue(null, previous); }
        }
        try {
            var mode = Enum.Parse(controller.GetNestedType("TransferMode", All), "NextLayer");
            controller.GetMethod("Request", All).Invoke(null, new object[] { null, mode });
            Check(false, "offline transfer rejected");
        } catch (TargetInvocationException e) {
            Check(e.InnerException is InvalidOperationException, "offline transfer rejected");
        }
    }

    static bool PreventWorldGeneration() => false;

    IEnumerator LayerUI() {
        Assembly mp = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "CasualtiesMP");
        Assembly game = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Assembly-CSharp");
        Type worldType = game.GetType("WorldGeneration", true);
        // Preserve the real user profile: this probe only loads scene UI and
        // explicitly skips the game's world-generation Start method.
        mp.GetType("Together.SavesystemPatch").GetField("PreserveSave", All).SetValue(null, true);
        var patch = new Harmony("together.hosttransfer.runtimechecks.skipgeneration");
        patch.Patch(AccessTools.Method(worldType, "Start"), prefix: new HarmonyMethod(typeof(HostTransferRuntimeChecks), nameof(PreventWorldGeneration)) { priority = Priority.First });
        SceneManager.LoadScene("SampleScene");
        yield return null;
        yield return new WaitForSecondsRealtime(2);
        var world = UnityEngine.Object.FindObjectOfType(worldType);
        Check(world != null, "native world scene loaded for exit UI inspection");
        if (world == null) yield break;
        var panel = (GameObject)worldType.GetField("savePanel", All).GetValue(world);
        Check(panel != null, "native layer save panel found");
        if (panel == null) yield break;
        foreach (var b in panel.GetComponentsInChildren<Button>(true))
            for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++) Logger.LogInfo("EXIT_BUTTON " + b.name + " -> " + b.onClick.GetPersistentMethodName(i));
        Type ui = mp.GetType("Together.HostTransferUI", true);
        var added = (Button)ui.GetMethod("AttachExitButton", All).Invoke(null, new object[] { panel });
        Check(added != null, "third transfer button attaches to actual native save/continue panel");
        if (added != null) {
            Check(added.GetComponentInChildren<TextMeshProUGUI>(true).text == "Transfer host...", "native exit button has intended player-visible label");
            Check(ReferenceEquals(added, ui.GetMethod("AttachExitButton", All).Invoke(null, new object[] { panel })), "native exit button attachment is idempotent");
            var allButtons = panel.GetComponentsInChildren<Button>(true);
            foreach (var b in allButtons) Logger.LogInfo("EXIT_RECT " + b.name + " " + ((RectTransform)b.transform).anchoredPosition + " " + ((RectTransform)b.transform).rect.size);
            var native = allButtons.First(b => b != added && b.onClick.GetPersistentEventCount() > 0);
            var addedRect = (RectTransform)added.transform;
            var nativeRect = (RectTransform)native.transform;
            Check(Mathf.Abs(addedRect.anchoredPosition.y - nativeRect.anchoredPosition.y) < 1f, "third exit button shares native save/continue row");
        }
        Type controller = mp.GetType("Together.LobbyHostTransfer", true);
        var plugin = BepInEx.Bootstrap.Chainloader.PluginInfos["CasualtiesMP"].Instance;
        object uiComponent = plugin.GetComponent(ui);
        var modeType = controller.GetNestedType("TransferMode", All);
        ui.GetMethod("ShowOffer", All).Invoke(null, new[] { (object)Guid.NewGuid().ToString("N"), "TestHost", Enum.ToObject(modeType, 0) });
        var dialog = (GameObject)ui.GetField("canvas", All).GetValue(uiComponent);
        Canvas.ForceUpdateCanvases();
        var text = dialog.GetComponentsInChildren<TextMeshProUGUI>(true);
        Check(text.Any(t => t.text.Contains("replace your active multiplayer expedition") && t.text.Contains("backed up")), "recipient sees replacement warning and backup promise in native dialog");
        var warning = text.First(t => t.text.Contains("replace your active multiplayer expedition"));
        float preferredHeight = warning.GetPreferredValues(warning.text, warning.rectTransform.rect.width, float.PositiveInfinity).y;
        Logger.LogInfo("WARNING_TEXT preferredHeight=" + preferredHeight + " rect=" + warning.rectTransform.rect.size + " font=" + warning.fontSize);
        Check(preferredHeight <= warning.rectTransform.rect.height + 1, "complete save warning fits its native dialog text area");
        Check(dialog.GetComponentsInChildren<Button>(true).Any(b => b.GetComponentInChildren<TextMeshProUGUI>(true)?.text == "Accept hosting and replace active save"), "recipient acceptance button names save replacement explicitly");
        ui.GetMethod("Open", All).Invoke(null, new object[] { false });
        dialog = (GameObject)ui.GetField("canvas", All).GetValue(uiComponent);
        Check(dialog.GetComponentsInChildren<TextMeshProUGUI>(true).Any(t => t.text.Contains("Include my saved multiplayer expedition")), "lobby picker includes optional expedition transfer control");
    }
}
