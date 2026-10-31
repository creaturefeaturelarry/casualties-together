


using CasualtiesTogetherUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;


public partial class KnownPersons : CasualtiesTogetherSingleton {
    public static KnownPersons inst { get; private set; }
    public static Sprite dune_stare = null;
    void Awake() {
        inst = this;
        dune_stare = CoopModAssets.LoadSprite("dune_stare.png");
    }
    void Start() {
        LoadEverything();
    }


    static readonly HashSet<ulong> STARS = new();
    static readonly HashSet<ulong> PLAYTESTERS = new();


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSteamUserPrivileged(ulong user) {
        return user == 0x110000112B2FDCD
            || user == 0x11000011CB9B7BE
            || user == 0x1100001345D812E
            ;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSteamUserMPDev(ulong user) {
        return
            user == 0x1100001345D812E
            ;
    }

    public static void LoadTagsFor(ScavPlayer plr) {
#if DEBUG
        if (DebugForced) {
            plr.KnownUserTagIcons.AddIfNotPresent(CoopModAssets.star_yellow);
            plr.KnownUserTagIcons.AddIfNotPresent(dune_stare);
        }
#endif
        if (Net.TryGetSteamTransport(out var tsteam)) {
            var id = plr.SteamId;
            if (STARS.Contains(plr.SteamId)) {
                plr.KnownUserTagIcons.AddIfNotPresent(CoopModAssets.star_yellow);
            }
            if (PLAYTESTERS.Contains(plr.SteamId)) {
                plr.KnownUserTagIcons.AddIfNotPresent(dune_stare);
            }
            if (IsSteamUserMPDev(plr.SteamId)) {
                plr.KnownUserTagIcons.AddIfNotPresent(CoopModAssets.mpmod_icon_green);
            }
        }
    }

    public static bool CanDoFancyNametagFor(ScavPlayer plr) {
#if DEBUG
        if (DebugForced) {
            return true;
        }
#endif
        if (!Net.IsRunningSteam) {
            return false;
        }
        if (STARS.Contains(plr.SteamId)) {
            return true;
        }
        if (IsSteamUserPrivileged(plr.SteamId)) {
            return true;
        }
        return false;
    }

    static string fileName => "casualtiestogether-d.bin";
    static string filePath => Path.Combine(BepInEx.Paths.CachePath, fileName);


    static void LoadEverything() {

        try {
            var read = CoopModAssets.ReadResource(fileName).ToStringFromCharBytes();
            AddStars(read);
        } catch (Exception e) {
#if TESTING
            log.error($"DEBUG: Resource Stars: {e}");
#endif
        }

        inst.SendMessage("ExtraKnownPersonsLoad", SendMessageOptions.DontRequireReceiver);
        if (File.Exists(filePath)) {
            LoadTheFile();
        }

    }

    static void LoadTheFile() {
        var file = File.ReadAllText(filePath);
        AddStars(file);
    }
    static void AddStars(string encrypted) {
        ReadFile(encrypted);

        foreach (var item in ScavPlayer.ClientIdToPlayerDict.Values) {
            if (item.body != null && KnownPersons.CanDoFancyNametagFor(item)) {
                CharStatusVisuals.MakeNametagFancy(item.netBody.visual.nametag.GetComponent<TextMeshPro>(), item);
            }

            LoadTagsFor(item);
        }
    }


    public class CreditsDataParsed {
        public class Playtester {
            public ulong steamid = 1;
            public string username;
        }
        public List<ulong> steam_ids = new(); // Supporters
        public List<Playtester> playtesters = new();
    }

    static CreditsDataParsed CreditsData = null;

    public static void ReadFile(string base64Data) {
        
        try {
            byte[] compressedBytes = Convert.FromBase64String(base64Data);

            using (var inputStream = new MemoryStream(compressedBytes)) {
                // Skip zlib header (2 bytes)
                inputStream.ReadByte();
                inputStream.ReadByte();

                using (var decompressor = new DeflateStream(inputStream, CompressionMode.Decompress))
                using (var reader = new StreamReader(decompressor)) {
                    
                    string result = reader.ReadToEnd();
                    try {
                        CreditsData = JsonConvert.DeserializeObject<CreditsDataParsed>(result);

                        if (CreditsData.steam_ids != null) {
#if TESTING
                            log.l($"Adding stars: [{String.Join(", ", CreditsData.steam_ids.Except(STARS))}]");
#endif
                            STARS.AddRange(CreditsData.steam_ids);
                        }
                        if (CreditsData.playtesters != null) {
                            var steamIds = CreditsData.playtesters.Select(x => x.steamid);
#if TESTING
                            log.l($"Adding playtesters: [{String.Join(", ", steamIds.Except(PLAYTESTERS))}]");
#endif
                            PLAYTESTERS.AddRange(steamIds);
                        }

                    } catch (Exception e) {
                        log.error($"Failed to read stars json: {result}\n{e}");
                    }
                }
            }
        } catch (Exception e) {
            log.error($"StarReadFile: {base64Data}\n{e.ToString()}");
        }

    }



#if DEBUG
    static bool DebugForced = true;
    [CTPostInit]
    static void RegisterDebugCommands() {
        
        Con.RegisterCommandModern("testforcestarson", "DEBUG MP - Force fancy nametags", (split, comm) => {
            DebugForced = !DebugForced;
            log.l("KnownPersons DebugForced", DebugForced.ToString());
        });
    }
#endif


}




