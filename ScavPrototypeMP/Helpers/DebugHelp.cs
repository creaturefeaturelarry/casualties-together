using HarmonyLib;
using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using LiteNetLib;
using UnityEngine;

namespace Together {

    public static class DebugMenuSettings {

#if !PLAYTEST 
        [SettingDeclarerThingyBool("setting_debug_verbose",allow_saving = false)]
#endif 
#if RELEASE && !PLAYTEST 
        internal static bool is_verbose = false;
#else 
        internal static bool is_verbose = true;
#endif 

        [SettingDeclarerThingyBool("setting_debug_events", allow_saving = false)]
#if RELEASE && !PLAYTEST 
        public static bool _DEV_VISUALISE_NET_EVENTS = false;
#else 
        public static bool _DEV_VISUALISE_NET_EVENTS = true;
#endif 

        [SettingDeclarerThingyBool("setting_debug_logsteam", allow_saving = false)]
#if RELEASE && !PLAYTEST 
        public static bool _DEV_ENABLE_STEAM_LOG = false;
#else 
        public static bool _DEV_ENABLE_STEAM_LOG = true;
#endif 


    }



    internal class log {
        public static bool verbose => Multiplayer.verbose;
        public static ConsoleScript con => ConsoleScript.instance;
        public const string name = "MP";
        public const string prefix1 = (name + ": ");


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string do_timestamp(string s) {
            return $"[{DateTime.UtcNow.ToString("HH:mm:ss.fff")}] {s}";
        }



        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void devevent(in string s, Vector2 eventpos, bool ignoreverbose = false) {
            devevent(s, eventpos, Color.cyan, ignoreverbose);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void devevent(string s, Vector2 eventpos, Color color, bool ignoreverbose) {
            
            Plugin.log.LogInfo(do_timestamp(s));

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(eventpos, s, color);

            if ((verbose || ignoreverbose) && con != null) {
                con.LogToConsole(prefix1 + s);
            }
        }


        public static void l(params string[] s) => l(String.Join(' ',s), true);
        public static void l(string s) => l(s, true);
        public static void l(string s, bool console) {
            s.Replace('\r', ' ');

            Plugin.log.LogInfo(do_timestamp(s));

            if (console && con != null) {
                con.LogToConsole(prefix1 + s);
            }
        }

        public static void warn(string s) => warn(s, true);
        public static void warn(string s, bool console) {
            s.Replace('\r', ' ');

            Plugin.log.LogWarning(do_timestamp(s));

            if (console && con != null) {
                con.LogToConsole($"<color=yellow>{name} WARN: {s}</color>");
            }

        }
        public static void error_devevent(in string s, in Vector2 pos) {
            s.Replace('\r', ' ');
            error(s, true);

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(pos, "ERROR: " + s, Color.red);
        }
        public static void error(Exception e) => error(e.ToString());
        public static void error(string s) => error(s, true);
        public static void error(string s, bool console) {
            s.Replace('\r', ' ');

            Plugin.log.LogError(do_timestamp(s));

            if (console && con != null) {
                con.LogToConsole($"<color=red>{name} ERROR: {s}</color>");
            }
        }
        public static void sus(string s, bool console = true) {
            s.Replace('\r', ' ');

            s = "SUS: " + s;
            Plugin.log.LogWarning(do_timestamp(s));

            if (console && con != null && Multiplayer.verbose) {
                con.LogToConsole($"<color=orange>{name}: {s}</color>");
            }

        }
        public static void serverdeny(string s) {
            s.Replace('\r', ' ');

            s = "SERVER DENY: " + s;
            Plugin.log.LogWarning(do_timestamp(s));

            if (con != null && Multiplayer.verbose) {
                con.LogToConsole($"<color=yellow>{name}: " + s + "</color>");
            }
        }
        public static void serverdeny(string s, Vector2 pos) {
            serverdeny(s);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(pos, "S: DENIED " + s, Color.red);
        }
    }



    public class DebugHelp : MonoBehaviour {


        public static string GetGameObjectFullDebugString(Component c) {
            return GetGameObjectFullDebugString(c.gameObject);
        }

        public static string GetGameObjectFullDebugString(GameObject go) {
            //var netid = new AnyObjectNetId(go);
            //if (netid.TryGetNetBody) 
                
            if (go.TryGetComponent(out Limb l)) {
                var bodyname = l.body.name;
                if (l.TryGetNetBody(out var nb)) {
                    bodyname = nb.ToString();
                }
                return $"LIMB({l.name} - {bodyname}, pos: {((Vector2)l.transform.position)})";
            }
            if (Util.TryGetBodyFromObject(go, out var b)) {
                return (GetPlayerFullDebugString(b));   
            }
            var tracker = go.GetComponent<SyncInfoGameObjectTracker>();
            return $"{(tracker != null ? tracker.ToString() : go.ToString())}";
        }
        public static string GetPlayerFullDebugString(Body body) {
            if (body == null) {
                return $"BODY(NULL)";
            }
            if (body.TryGetNetBody(out var nb)) {
                return nb.ToString();
            }
            return $"BODY({body.name}, pos: {((Vector2)body.transform.position)})";
        }
        public static string GetPlayerFullDebugString(knetid cid) {
            if (ScavPlayer.TryGetPlayerFromClientId(cid, out var plr)) {
                return plr.ToString();
            } else {
                return $"PLR(ID:{cid}, UNKNOWN)";
            }
        }







        private class EventDisplayInfo {
            public Vector2 pos;
            public string text;
            public float duration;
            public UnityEngine.Color color;
            public double start;
        }

        private static List<EventDisplayInfo> events = new List<EventDisplayInfo>();  

        public static bool _DEV_VISUALISE_NET_EVENTS => DebugMenuSettings._DEV_VISUALISE_NET_EVENTS;

        /// <summary>
        /// THIS SHOULD BE CALLED ONLY ON RECEIVE, SO THAT I KNOW WHAT IS RELATED TO A MESSAGE THAT IS RELAYED OR JUST NOT MINE
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="text"></param>
        /// <param name="color"></param>
        /// <param name="duration"></param>
        public static void OnNetEvent(Vector2 pos, in string text, UnityEngine.Color? color = null , float duration = 2f) {
            if (_DEV_VISUALISE_NET_EVENTS) {
                while (CheckOverlap(ref pos)) {  } // repeat checkoverlap until theres no overlap

                events.Add(new EventDisplayInfo {
                    pos = pos,
                    text = text,
                    duration = duration,
                    color = color ?? UnityEngine.Color.blue,
                    start = Time.realtimeSinceStartupAsDouble,
                }); 
            }
        }

        static bool CheckOverlap(ref Vector2 pos) {
            foreach (var item in events) {
                if (KM.DistanceCheck(item.pos, pos,1)) {
                    pos += Vector2.up;
                    return true;
                }
            }
            return false;
        }





        void OnGUI() {
            if (_DEV_VISUALISE_NET_EVENTS && Net.IsRunning) {

                var ogfont = GUI.skin.label.fontSize;

                foreach (var item in events) {
                    var pos = (Vector2)Camera.main.WorldToScreenPoint(item.pos);
                    pos = new Vector2(pos.x, Screen.height - pos.y);

                    if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) {

                        GUI.skin.label.normal.textColor = item.color;
                        GUI.skin.label.fontSize = 10;
                        GUI.Label(new Rect(pos.x, pos.y, 600, 100), item.text);
                        
                    }
                }
                events.RemoveAll(i => Time.realtimeSinceStartupAsDouble - i.start > i.duration);

                GUI.skin.label.normal.textColor = UnityEngine.Color.white;
                GUI.skin.label.fontSize = ogfont;
            }
        }


        public static void Server_SendAnEmptyDebugEvent(in Vector2 pos, in string msg, IReadOnlyList<knetid> target_clients = null ) {
            if (target_clients == null) {
                target_clients = ServerMain.AllClientIdsExceptHost;
            }

            var writer = Net.CreateWriter(NetmsgId.CLIENT_EmptyDebugEvent);

            writer.Put(pos);
            writer.Put(msg, true);

            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: target_clients);
            // WRITER END;
        }


        [ClientReceiver((byte)NetmsgId.CLIENT_EmptyDebugEvent)]
        static void Client_EmptyDebugEvent(knetid _, LiteNetLib.Utils.NetDataReader reader) {
            reader.Get(out Vector2 pos);
            reader.Get(out string msg, true);
           
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(pos, msg);
            
        }

    }
}
