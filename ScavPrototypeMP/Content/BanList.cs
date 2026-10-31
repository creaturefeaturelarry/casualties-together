using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace Together;


public class BanList :CasualtiesTogetherSingleton {
    public static BanList Instance { get; private set; }

    public string filePath { get; private set; }

    [Serializable]
    public class BanEntry {
        public string ip;
        public string name;
        public ulong steamid;

        public bool HasIP() {
            return !string.IsNullOrEmpty(ip) && ip != "NULL";
        }

        public override string ToString() {
            var str = "";
            if (steamid != 0) {
                str += $"SteamID:{steamid}  ";
            }
            if (HasIP()) {
                str += $"IP:{ip}  ";
            }
            str += $"Name:{name}";
            return str;
        }
    }

    [Serializable]
    private class PrefData {
        public List<BanEntry> data = new();
    }

    private PrefData prefs = new PrefData();

    public static IReadOnlyList<BanEntry> Entries => Instance.prefs.data;

    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        filePath = Path.Combine(Application.persistentDataPath, "mp_banlist.json");
        Load();
    }

    // -------- STATIC API --------

    public static void Add(string ip, string name, ulong steamid) {
        if (Instance == null) return;

        // prevent duplicates
        if (steamid != 0 && Instance.prefs.data.Exists(e => e.steamid == steamid))
            return;

        var entry = new BanEntry {
            ip = ip,
            name = name,
            steamid = steamid
        };

        Instance.prefs.data.Add(entry);
        Instance.Save();
    }

    public static bool RemoveBySteamId(ulong steamid) {
        if (Instance == null) return false;

        var entry = Instance.prefs.data.Find(e => e.steamid == steamid);
        if (entry == null) return false;

        Instance.prefs.data.Remove(entry);
        Instance.Save();
        return true;
    }
    public static bool Remove(BanEntry entry) {
        if (Instance == null) return false;

        var aaa = Instance.prefs.data.Remove(entry);
        Instance.Save();
        return aaa;
    }

    public static bool IsBanned(ulong steamid) {
        if (Instance == null || steamid == 0) return false;

        var list = Instance.prefs.data;

        for (int i = 0; i < list.Count; i++) {
            if (list[i].steamid == steamid)
                return true;
        }

        return false;
    }
    public static bool IsBannedIPName(string ip, string name) {
        if (Instance == null) return false;

        var list = Instance.prefs.data;

        for (int i = 0; i < list.Count; i++) {
            var entry = list[i];
            if (entry.HasIP()) {
                if (entry.ip == ip) {
                    return true;
                }
            } else {
                if (entry.name == name) {
                    return true;
                }
            }
        }

        return false;
    }

    public static void ClearAll() {
        if (Instance == null) return;

        Instance.prefs.data.Clear();
        Instance.Save();
    }

    // -------- INTERNAL --------

    private void Save() {
        try {
            string json = JsonConvert.SerializeObject(prefs, Formatting.Indented);
            File.WriteAllText(filePath, json);
        } catch (Exception e) {
            Debug.LogError("BanList Save failed: " + e);
        }
    }

    private void Load() {
        try {
            if (File.Exists(filePath)) {
                string json = File.ReadAllText(filePath);
                prefs = JsonConvert.DeserializeObject<PrefData>(json) ?? new PrefData();
                return;
            }
        } catch (Exception e) {
            Debug.LogError("BanList Load failed: " + e);
        }

        prefs = new PrefData();
    }

}










