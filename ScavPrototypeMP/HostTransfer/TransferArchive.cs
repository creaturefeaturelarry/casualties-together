using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Together
{
    internal static class TransferArchive
    {
        internal const int MaxBytes = 64 * 1024 * 1024;
        internal const int MaxExpanded = 128 * 1024 * 1024;
        internal const int MaxSaveJson = 16 * 1024 * 1024;
        internal const string FreshHostKey = "TRANSFER_FRESH_HOST";
        private static readonly string[] WorldFiles = { "world_tiles.bin", "world_fluid.bin", "world_objects.bin", "qol_worldgen_state.txt" };
        private static bool PlayerDirectory(string name) => name.StartsWith("STEAM_", StringComparison.Ordinal)
            && ulong.TryParse(name.Substring(6), NumberStyles.None, CultureInfo.InvariantCulture, out ulong steam)
            && steam != 0 && name == "STEAM_" + steam;

        // Keep recovery files outside the active save and on the same volume so
        // installation and rollback can still move whole directories atomically.
        internal static string RecoveryFolder(string root, string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid transfer ID.");
            return Path.Combine(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "_host_transfer_backups", id);
        }

        // save.sv is itself gzip compressed. Bounding only the outer ZIP does not
        // bound the memory needed to parse its JSON.
        internal static JObject ReadSave(string path)
        {
            if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Player save too large.");
            using (var file = File.OpenRead(path))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[8192]; int n;
                while ((n = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + n > MaxSaveJson) throw new InvalidDataException("Expanded player save too large.");
                    output.Write(buffer, 0, n);
                }
                return JObject.Parse(Encoding.ASCII.GetString(output.ToArray()));
            }
        }

        internal static void ValidateCheckpoint(string root, ulong owner, int depth, IEnumerable<ulong> players)
        {
            var rules = JObject.Parse(File.ReadAllText(Path.Combine(root, "mp_rules.json")));
            if (rules["LEVEL"]?.Type != JTokenType.Boolean || rules.Value<bool>("LEVEL") ||
                rules["RULES"]?.Type != JTokenType.Object || rules.Value<int?>("MODIFIER") != -1 ||
                !(rules["PLRPOS"] is JObject positions) || positions.Count != 0)
                throw new InvalidDataException("Not a next-layer checkpoint.");
            foreach (ulong steam in players)
                if (ReadSave(Path.Combine(root, "STEAM_" + steam, "save.sv")).Value<int?>("biome") != depth)
                    throw new InvalidDataException("Player checkpoint layer mismatch.");
            byte[] main = File.ReadAllBytes(Path.Combine(root, "save.sv"));
            byte[] personal = File.ReadAllBytes(Path.Combine(root, "STEAM_" + owner, "save.sv"));
            if (!main.SequenceEqual(personal)) throw new InvalidDataException("Root save is not the new host's character.");
        }
        internal static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        internal static void CopyTree(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(source.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                string output = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.Copy(file, output, false);
            }
        }

        // Only run/player checkpoint files belong in a next-layer handoff. Old terrain
        // files, positions and QoL slot sidecars must not resurrect the previous layer.
        internal static byte[] Build(string root, ulong target, int depth, IEnumerable<ulong> players)
        {
            string[] ids = players.Select(p => "STEAM_" + p).ToArray();
            if (depth < 1 || depth > 10 || target == 0 || !ids.Contains("STEAM_" + target))
                throw new InvalidDataException("Invalid next layer or recipient.");
            foreach (string id in ids)
                if (!File.Exists(Path.Combine(root, id, "save.sv")))
                    throw new InvalidDataException("Missing current player checkpoint: " + id);
            ids = ids.Concat(Directory.GetDirectories(root).Select(Path.GetFileName)
                .Where(n => PlayerDirectory(n)
                    && File.Exists(Path.Combine(root, n, "save.sv")))).Distinct().ToArray();
            if (ids.Length > 250) throw new InvalidDataException("Too many saved players.");
            var rules = JObject.Parse(File.ReadAllText(Path.Combine(root, "mp_rules.json")));
            rules.Remove(FreshHostKey);
            rules["LEVEL"] = false; rules["MODIFIER"] = -1; rules["PLRPOS"] = new JObject();
            using (var output = new MemoryStream())
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    Write(zip, "mp_rules.json", System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(rules)));
                    long total = 0, jsonTotal = 0;
                    foreach (string id in ids)
                    {
                        string path = Path.Combine(root, id, "save.sv");
                        if ((total += new FileInfo(path).Length) > MaxBytes) throw new InvalidDataException("Checkpoint too large.");
                        JObject save = ReadSave(path);
                        if (save["biome"] == null) throw new InvalidDataException("Checkpoint has no layer.");
                        save["biome"] = depth; // progression already happened in RegenerateWorld
                        string json = JsonConvert.SerializeObject(save);
                        if ((jsonTotal += json.Length) > MaxExpanded) throw new InvalidDataException("Expanded checkpoint too large.");
                        byte[] bytes = SaveSystem.Zip(json);
                        Write(zip, id + "/save.sv", bytes);
                        if (id == "STEAM_" + target) Write(zip, "save.sv", bytes);
                    }
                }
                if (output.Length > MaxBytes) throw new InvalidDataException("Checkpoint too large.");
                return output.ToArray();
            }
        }

        // A lobby handoff copies a saved expedition exactly as it is on disk.
        // It must not advance depth or strip a mid-layer save's terrain/positions.
        internal static byte[] BuildSaved(string root, ulong target)
        {
            if (target == 0) throw new InvalidDataException("Invalid save recipient.");
            var rules = JObject.Parse(File.ReadAllText(Path.Combine(root, "mp_rules.json")));
            if (rules["RULES"]?.Type != JTokenType.Object || rules["LEVEL"]?.Type != JTokenType.Boolean)
                throw new InvalidDataException("The host has no readable multiplayer expedition.");
            string personal = Path.Combine(root, "STEAM_" + target, "save.sv");
            bool hasCharacter = File.Exists(personal);
            byte[] main = File.ReadAllBytes(hasCharacter ? personal : Path.Combine(root, "save.sv"));
            ReadSave(hasCharacter ? personal : Path.Combine(root, "save.sv"));
            rules.Remove(FreshHostKey);
            if (!hasCharacter) rules[FreshHostKey] = target.ToString(CultureInfo.InvariantCulture);
            var playerDirs = Directory.GetDirectories(root).Where(p => PlayerDirectory(Path.GetFileName(p)) && File.Exists(Path.Combine(p, "save.sv"))).ToArray();
            if (playerDirs.Length > 250) throw new InvalidDataException("Too many saved players.");
            using (var output = new MemoryStream())
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    Write(zip, "mp_rules.json", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(rules)));
                    Write(zip, "save.sv", main);
                    long total = main.Length;
                    foreach (string dir in playerDirs)
                    {
                        string path = Path.Combine(dir, "save.sv"); ReadSave(path);
                        byte[] bytes = File.ReadAllBytes(path);
                        if ((total += bytes.Length) > MaxExpanded) throw new InvalidDataException("Saved expedition too large.");
                        Write(zip, Path.GetFileName(dir) + "/save.sv", bytes);
                    }
                    foreach (string name in WorldFiles)
                    {
                        string path = Path.Combine(root, name); if (!File.Exists(path)) continue;
                        if ((total += new FileInfo(path).Length) > MaxExpanded) throw new InvalidDataException("Saved expedition too large.");
                        Write(zip, name, File.ReadAllBytes(path));
                    }
                }
                if (output.Length > MaxBytes) throw new InvalidDataException("Saved expedition too large.");
                return output.ToArray();
            }
        }

        internal static void ValidateSaved(string root, ulong recipient)
        {
            var header = JObject.Parse(File.ReadAllText(Path.Combine(root, "mp_rules.json")));
            if (header["RULES"]?.Type != JTokenType.Object || header["LEVEL"]?.Type != JTokenType.Boolean)
                throw new InvalidDataException("Invalid multiplayer save header.");
            var save = ReadSave(Path.Combine(root, "save.sv"));
            if (save["biome"]?.Type != JTokenType.Integer) throw new InvalidDataException("Saved expedition has no layer.");
            string personal = Path.Combine(root, "STEAM_" + recipient, "save.sv");
            if (File.Exists(personal))
            {
                if (header[FreshHostKey] != null || !File.ReadAllBytes(personal).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "save.sv"))))
                    throw new InvalidDataException("Saved expedition has the wrong host character.");
            }
            else if (header.Value<string>(FreshHostKey) != recipient.ToString(CultureInfo.InvariantCulture))
                throw new InvalidDataException("Missing recipient character policy.");
            long expandedJson = 0;
            foreach (string dir in Directory.GetDirectories(root).Where(p => PlayerDirectory(Path.GetFileName(p))))
            {
                var body = ReadSave(Path.Combine(dir, "save.sv"));
                if ((expandedJson += JsonConvert.SerializeObject(body).Length) > MaxExpanded) throw new InvalidDataException("Expanded saves too large.");
            }
        }

        internal static string SavedRunSettings(string root)
        {
            var settings = ReadSave(Path.Combine(root, "save.sv"))["runSettings"] as JArray;
            if (settings == null) return null;
            byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(settings));
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true)) gzip.Write(bytes, 0, bytes.Length);
                return Convert.ToBase64String(output.ToArray());
            }
        }

        internal static bool NeedsFreshHost(string root, ulong recipient)
        {
            if (File.Exists(Path.Combine(root, "STEAM_" + recipient, "save.sv"))) return false;
            var header = JObject.Parse(File.ReadAllText(Path.Combine(root, "mp_rules.json")));
            return header.Value<string>(FreshHostKey) == recipient.ToString(CultureInfo.InvariantCulture);
        }

        internal static void NormalizeSource(string root, ulong host, int depth, IEnumerable<ulong> players, string id)
        {
            // SaveGame writes biomeDepth+1; regeneration already advanced depth.
            // Keep the original host's recovery save at the same depth as the recipient.
            byte[] bytes = Build(root, host, depth, players);
            string staged = Stage(bytes, root, id);
            ValidateCheckpoint(staged, host, depth, players);
            Install(staged, root, id);
        }

        private static void Write(ZipArchive zip, string name, byte[] bytes)
        {
            using (Stream s = zip.CreateEntry(name).Open()) s.Write(bytes, 0, bytes.Length);
        }

        internal static string Stage(byte[] bytes, string root, string id)
        {
            string stage = Path.Combine(RecoveryFolder(root, id), "staged-save");
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Checkpoint too large.");
            if (Directory.Exists(stage)) throw new IOException("Transfer staging already exists.");
            Directory.CreateDirectory(stage);
            long expanded = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > 260) throw new InvalidDataException("Too many checkpoint files.");
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName;
                    bool player = name.EndsWith("/save.sv", StringComparison.Ordinal)
                        && PlayerDirectory(name.Substring(0, name.Length - 8));
                    if ((!player && name != "save.sv" && name != "mp_rules.json" && !WorldFiles.Contains(name)) || !names.Add(name))
                        throw new InvalidDataException("Unexpected/duplicate checkpoint path.");
                    if (entry.Length < 0 || (expanded += entry.Length) > MaxExpanded) throw new InvalidDataException("Expanded checkpoint too large.");
                    string path = Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open())
                    using (var output = File.Create(path))
                    {
                        byte[] buffer = new byte[8192]; long written = 0; int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if ((written += n) > entry.Length) throw new InvalidDataException("Invalid checkpoint length.");
                            output.Write(buffer, 0, n);
                        }
                        if (written != entry.Length) throw new InvalidDataException("Truncated checkpoint file.");
                    }
                }
            }
            if (!names.Contains("save.sv") || !names.Contains("mp_rules.json")) throw new InvalidDataException("Incomplete checkpoint.");
            return stage;
        }

        internal static void Install(string stage, string root, string id)
        {
            string recovery = RecoveryFolder(root, id);
            if (stage != Path.Combine(recovery, "staged-save"))
                throw new InvalidDataException("Invalid staging path.");
            Directory.CreateDirectory(recovery);
            string backup = Path.Combine(recovery, "previous-save");
            if (Directory.Exists(root)) Directory.Move(root, backup);
            try { Directory.Move(stage, root); }
            catch { if (!Directory.Exists(root) && Directory.Exists(backup)) Directory.Move(backup, root); throw; }
        }
    }
}
