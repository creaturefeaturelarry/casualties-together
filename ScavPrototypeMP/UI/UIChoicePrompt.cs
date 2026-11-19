using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using Together;
using UnityEngine;
using UnityEngine.UI;

namespace Together;



public class UIChoicePrompt : CasualtiesTogetherSingleton {

    public static float ScreenHeight {get{
        if (UIBullshit.USE_NEW_UI) {

        }
        return Screen.height;
    }}
    public static float ScreenWidth {get{
        if (UIBullshit.USE_NEW_UI) {
            
        }
        return Screen.width;
    }}

    public static void ClientDoNetPrompt(uint key, string promptid, string title, string text) {
        Lang.MsgTryParse(ref title);
        Lang.MsgTryParse(ref text);

#if TESTING
        log.l($"ClientDoNetPrompt:\n{title}\n{text}");
#endif

        UIChoicePrompt.ShowPrompt(new UIChoicePrompt.Prompt {
            text = text,
            name = title,
            //defaultcanignore = true,
            timeout = 12,
            pos = new Vector2(0.5f, 0.98f),
            size = new Vector2(0.43f, 0.18f),
            choices = new UIChoicePrompt.Prompt.PromptChoice[] {
                new UIChoicePrompt.Prompt.PromptChoice {
                    text = Lang.Get("accept"),
                    action = () => {
                        var writer = Net.CreateWriter(NetmsgId.SERVER_CustomPromptAnswer);
                        writer.Put(key);
                        writer.Put(promptid);
                        writer.Put((byte)0);
                        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    }
                },
                new UIChoicePrompt.Prompt.PromptChoice {
                    text = Lang.Get("decline"),
                    action = () => {
                        var writer = Net.CreateWriter(NetmsgId.SERVER_CustomPromptAnswer);
                        writer.Put(key);
                        writer.Put(promptid);
                        writer.Put((byte)1);
                        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    }
                },
            },
        });
    }












    public static UIChoicePrompt instance { get; private set; }
    public UIChoicePrompt() {
        instance = this;
    }

    public class Prompt {
        public class PromptChoice {
            public Dictionary<string,object> data;
            public string text;
            public Action action;
        }
        public readonly Dictionary<string,object> data = new();
        public string name;
        public string text;
        public float timeout = 5;
        public Texture2D texture;
        public Vector2 pos = new Vector2(0.2f, 0f);
        public Vector2 size = new Vector2(0.35f, 0.16f);
        public bool immidiate; // if true it doesnt wait for mouse to move to prevent accidental click
        public int defaultchoice = -1;
        public bool defaultcanignore;
        public byte chosenchoice = 0;
        public bool chosenignore;
        public PromptChoice[] choices = null;

        public void CalculateRectTransform(RectTransform rt, bool use_progress) {
            var realPromptSize = size * UIManager.ReferenceResolution;

            var pos = this.pos;
            pos = new Vector2(pos.x, 1 - pos.y);

            // testprompt aaa bbb 1 0

            rt.pivot = pos;
            var realPos = pos;
            if (use_progress) {
                if (pos.y < 0.5f) {
                    rt.pivot = Vector2.LerpUnclamped(new Vector2(rt.pivot.x, 1), rt.pivot, prompt_show_progress);
                    realPos = Vector2.LerpUnclamped(new Vector2(realPos.x, 0), realPos, prompt_show_progress);
                } else {
                    rt.pivot = Vector2.LerpUnclamped(new Vector2(rt.pivot.x, 0), rt.pivot, prompt_show_progress);
                    realPos = Vector2.LerpUnclamped(new Vector2(realPos.x, 1), realPos, prompt_show_progress);
                }
            }

            rt.anchorMin = realPos;
            rt.anchorMax = realPos;
            rt.SetSizeAndPos(realPromptSize, Vector2.one*0.5f);
        }


        public Rect CalculateRect(bool use_progress) {

            var sss = Mathf.Min(ScreenHeight, ScreenWidth);

            var areawidth = sss * size.x;
            var areaheight = sss * size.y;

            var real_pos_X = (ScreenWidth - areawidth) * pos.x;
            var real_pos_Y = (ScreenHeight - areaheight) * pos.y;
            if (use_progress) {
                if (pos.y < 0.5f) {
                    real_pos_Y = Mathf.Lerp(-areaheight, real_pos_Y, prompt_show_progress);
                } else {
                    real_pos_Y = Mathf.Lerp(ScreenHeight, real_pos_Y, prompt_show_progress);
                }
            }

            Rect rect = new Rect(real_pos_X, real_pos_Y, areawidth, areaheight);

            return rect;
        }
    }

    Queue<Prompt> prompt_queue = new();
    public static float prompt_show_progress = 0;
    public static bool mouse_is_near_area = false;

    public static void ShowPrompt(Prompt prompt) {
        instance.prompt_queue.Enqueue(prompt);
    }

    void Update() {
        mouse_is_near_area = false;
        if (prompt_queue.Count > 0) {
            var firstprompt = prompt_queue.Peek();
            if ( firstprompt.timeout <= 0 ) {
                prompt_show_progress = Mathf.MoveTowards(prompt_show_progress, 0, Time.deltaTime * 4f);

                if (prompt_show_progress == 0) {
                    prompt_queue.Dequeue();
                }
            } else {
                if (prompt_show_progress < 1) {
                    if (firstprompt.immidiate) {
                        prompt_show_progress = 1;
                    } else {

                        var rrr = firstprompt.CalculateRect(false).ShrinkBorder(-20 * UIMainMenu.GetMenuUIScale());
                        mouse_is_near_area = UIBullshit.IsCursorInGUIRect(rrr);

                        var speed = 3f;

                        if (!mouse_is_near_area || prompt_show_progress == 0) {
                            speed = 3f;
                        } else {
                            speed = 0.2f;
                        }
                        prompt_show_progress = Mathf.MoveTowards(prompt_show_progress, 1, Time.deltaTime * speed);

                        if (prompt_show_progress >= 1) {
                            Util.PlayUISound(PlayerCamera.UISoundType.Click);
                        }
                    }
                } else {
                    if (firstprompt.timeout > 0) {
                        firstprompt.timeout -= Time.deltaTime;
                        if (firstprompt.timeout <= 0 ) {
                            if (firstprompt.defaultcanignore) {
                                firstprompt.chosenignore = true;
                            } else {
                                PromptUse(firstprompt, firstprompt.defaultchoice);
                            }
                            Util.PlayUISound(PlayerCamera.UISoundType.Close);
                        }
                    }
                }
            }
        } else {
            prompt_show_progress = 0;
        }

    }

    public static void PromptUse(Prompt prompt, int choice) {
        var ch = (byte)choice;

        if (prompt.choices != null) {
            if (ch >= prompt.choices.Count()) {
                ch = (byte)(prompt.choices.Count() - 1);
            }

            SafeActionInvoke(prompt.choices[ch].action);
        }

        prompt.chosenchoice = ch;  
        prompt.timeout = 0;
    }
    public static void SafeActionInvoke(Action action) {
        try {
            action.Invoke();
        } catch (Exception e) {
            log.error(e);
        }
    }


    public static IEnumerator DoPromptCoroutine(Prompt prompt) {
        ShowPrompt(prompt);
        while (instance.prompt_queue.Contains(prompt)) {
            yield return null;
        }
    }

    public static Task DoPromptAsync(Prompt prompt) {
        return Util.StartCoroutineAsync(DoPromptCoroutine(prompt));
    }


    internal void _GUI_RenderUIChoicePrompt() {
        FixChoicePrompt.Begin();
        try {

        if (prompt_show_progress != 0) {
            if (prompt_queue.Count > 0) {
                var ogcolor = GUI.color;
                var ogwrap = GUI.skin.label.wordWrap;
                var ogrich = GUI.skin.label.richText;
                var ogalignment = GUI.skin.label.alignment;

                var firstprompt = prompt_queue.Peek();
                if (prompt_show_progress < 1) {
                    if (mouse_is_near_area) {
                        var aaaaa = Mathf.Sin(Time.realtimeSinceStartup*6f);
                        GUI.color = new Color(aaaaa, aaaaa, aaaaa, 0.7f + aaaaa * 0.3f);
                        var shadowrect = firstprompt.CalculateRect(false);
                        GUI.Label(shadowrect, "", GUI.skin.box);
                        
                        GUI.color = ogcolor;

                        GUI.skin.label.alignment = TextAnchor.LowerCenter;
                        GUI.Label(shadowrect, Lang.Get("movemouse"));

                        UIBullshit._GUI_SetTooltip(Lang.Get("movemouse"),"");
                    }
                }
                GUI.skin.label.alignment = TextAnchor.UpperCenter;

                var rect = firstprompt.CalculateRect(true).ShrinkBorder(3 * UIMainMenu.GetMenuUIScale());
                //GUI.Label(rect, "", GUI.skin.box);

                UIBullshit.CheckCursorOverlap(rect);
                GUILayout.BeginArea(rect,"", GUI.skin.box);

                GUI.skin.label.richText = true;
                UIBullshit._GUI_BiggerLabel(firstprompt.name ,1.4f);

                GUI.skin.label.wordWrap = true;
                GUI.skin.label.richText = true;
                
                GUILayout.Label( firstprompt.text );
                GUI.skin.label.wordWrap = ogwrap;


                if (firstprompt.timeout > 0 && firstprompt.choices != null) {
                    GUILayout.BeginVertical();
                    GUILayout.FlexibleSpace();
                    GUILayout.EndVertical();

                    var og_enabled = GUI.enabled;
                    if (prompt_show_progress < 1 && mouse_is_near_area) {
                        GUI.enabled = false;
                        var aaaaa = Mathf.Sin(Time.realtimeSinceStartup * 6f);
                        GUI.color = new Color(1, 1, 1, 0.2f + aaaaa * 0.15f);
                    }

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    for (int i = 0; i < firstprompt.choices.Length; i++) {
                        var item = firstprompt.choices[i];
                        if (GUILayout.Button(item.text)) {
                            PromptUse(firstprompt,i);
                            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        }
                        GUILayout.FlexibleSpace();
                    }
                    GUILayout.EndHorizontal();

                    GUI.enabled = og_enabled;
                    GUI.color = ogcolor;
                }
                GUILayout.EndArea();
                GUI.skin.label.alignment = ogalignment;
                GUI.skin.label.richText = ogrich;
            }
        }

        } finally {
            FixChoicePrompt.End();
        }
    }


}


public class VoteSystem :CasualtiesTogetherSingleton {




    public delegate void VoteEndAction(HashSet<ScavPlayer> voted_yes, HashSet<ScavPlayer> voted_no, HashSet<ScavPlayer> voted_ignore);

    static ushort _voteCounter = 0;

    public class Server_VoteState {
        public ushort id;
        public float time;

        public VoteEndAction endAction;

        public HashSet<ScavPlayer> voters;
        public HashSet<ScavPlayer> votedPositive = new();
        public HashSet<ScavPlayer> votedNegative = new();
    }

    public static Server_VoteState Server_ActiveVote = null;

    void Start() {

        ServerMain.ServerCustomCommandsDict.Add("vote", (plr, cmd, argv) => {
            var callername = "SERVER";
            if (plr != null) {
                callername = plr.playerName;
            }


            if (argv[1] == "kick") {
                var plrname = argv[2];
                var res = Con.PerformActionOnPlayersByName(plrname, (ScavPlayer oplr) => {
                    if (!oplr.IsLocal) {
                        Chat.Server_ChatAnnouncement($"{callername} called a vote kick for {oplr.playerName}");
                        Server_VoteKick(oplr, plr);
                    }
                }, false, plr, false, true);
                
            } else if (argv[1] == "mutevc") {
                var plrname = argv[2];
                var res = Con.PerformActionOnPlayersByName(plrname, (ScavPlayer oplr) => {
                    if (!oplr.IsLocal) {
                        Chat.Server_ChatAnnouncement($"{callername} called a vote mute vc for {oplr.playerName}");
                        Server_VoteMuteVC(oplr, plr);
                    }
                }, false, plr, false, true);

            } else if (argv[1] == "mutetc") {
                var plrname = argv[2];
                var res = Con.PerformActionOnPlayersByName(plrname, (ScavPlayer oplr) => {
                    if (!oplr.IsLocal) {
                        Chat.Server_ChatAnnouncement($"{callername} called a vote mute tc for {oplr.playerName}");
                        Server_VoteMuteTC(oplr, plr);
                    }
                }, false, plr, false, true);

            }

        });
    }

    public static void Server_VoteKick(ScavPlayer plr, ScavPlayer who_called_the_vote = null) {
        var txt = $"Kick {plr.playerName}";
        

        Server_AnnounceVote("Vote Kick", txt, 10, (HashSet<ScavPlayer> voted_yes, HashSet<ScavPlayer> voted_no, HashSet<ScavPlayer> voted_ignore) => {
            voted_ignore.Remove(plr);
            var total = (float)(voted_yes.Count + voted_no.Count + voted_ignore.Count);
            var yes = (float)voted_yes.Count;
            if (total > 0) {
                var perc = yes / total;

                var percentStr = Mathf.RoundToInt(perc * 100f).ToString();
                Chat.Server_ChatAnnouncement($"Vote kick result: {percentStr}%");
                if (perc > 0.5f) {
                    if (plr != null) {
                        plr.Server_Kick($"Vote kicked. {percentStr}%");
                    }
                }
            }
        });
    }
    public static void Server_VoteMuteVC(ScavPlayer plr, ScavPlayer who_called_the_vote = null) {
        var txt = $"Mute {plr.playerName}";
        

        Server_AnnounceVote("Vote Mute VC", txt, 10, (HashSet<ScavPlayer> voted_yes, HashSet<ScavPlayer> voted_no, HashSet<ScavPlayer> voted_ignore) => {
            voted_ignore.Remove(plr);
            var total = (float)(voted_yes.Count + voted_no.Count + voted_ignore.Count);
            var yes = (float)voted_yes.Count;
            if (total > 0) {
                var perc = yes / total;

                Chat.Server_ChatAnnouncement($"Vote mute vc result: {Mathf.RoundToInt(perc * 100f)}%");
                if (perc > 0.5f) {
                    if (plr != null) {
                        plr.server_mute_vc = true;
                    }
                }
            }
        });
    }
    public static void Server_VoteMuteTC(ScavPlayer plr, ScavPlayer who_called_the_vote = null) {
        var txt = $"Mute {plr.playerName}";
        

        Server_AnnounceVote("Vote Mute TC", txt, 10, (HashSet<ScavPlayer> voted_yes, HashSet<ScavPlayer> voted_no, HashSet<ScavPlayer> voted_ignore) => {
            voted_ignore.Remove(plr);
            var total = (float)(voted_yes.Count + voted_no.Count + voted_ignore.Count);
            var yes = (float)voted_yes.Count;
            if (total > 0) {
                var perc = yes / total;

                Chat.Server_ChatAnnouncement($"Vote mute tc result: {Mathf.RoundToInt(perc * 100f)}%");
                if (perc > 0.5f) {
                    if (plr != null) {
                        plr.server_mute_tc = true;
                    }
                }
            }
        });
    }

    public static void Server_AnnounceVote(in string title, in string message, in float timetovote, in VoteEndAction action, IReadOnlyList<ScavPlayer> voters = null) {
        if (Net.IsClient) {
            throw new Exception("Client can't do that!");
        }
        if (Server_ActiveVote != null) {
            throw new Exception("Already voting");
        }

        voters ??= ScavPlayer.ClientIdToPlayerDict.Values.ToList();

        if (voters.Count > 0) {
            Server_ActiveVote = new Server_VoteState() {
                id = _voteCounter++,
                time = timetovote + 1,
                endAction = action, 
                voters = new HashSet<ScavPlayer>(voters),
            };


            var writer = Net.CreateWriter(NetmsgId.CLIENT_AnnounceVote);
            writer.Put(Server_ActiveVote.id);
            writer.Put(title);
            writer.Put(message);
            writer.Put(timetovote);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, voters);

        } else {
            action.Invoke(null, null, null);
        }

    }



    [ClientReceiver((byte)NetmsgId.CLIENT_AnnounceVote, false)]
    static void ClientReceiver__AnnounceVote(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        var voteid = (reader.GetUShort());
        var title = (reader.GetString());
        var message = (reader.GetString());
        var timetovote = (reader.GetFloat());

        Lang.MsgTryParse(ref title);
        Lang.MsgTryParse(ref message);



        UIChoicePrompt.ShowPrompt(new UIChoicePrompt.Prompt {
            text = message,
            name = title,
            timeout = timetovote,
            defaultcanignore = true,
            choices = new UIChoicePrompt.Prompt.PromptChoice[] {
                new UIChoicePrompt.Prompt.PromptChoice {
                    text = Lang.Get("yes"),
                    action = () => {
                        var writer = Net.CreateWriter(NetmsgId.SERVER_Vote);
                        writer.Put(voteid);
                        writer.Put((byte)0);
                        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    }
                },
                new UIChoicePrompt.Prompt.PromptChoice {
                    text = Lang.Get("no"),
                    action = () => {
                        var writer = Net.CreateWriter(NetmsgId.SERVER_Vote);
                        writer.Put(voteid);
                        writer.Put((byte)1);
                        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    }
                },
            },
        });
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Vote)]
    static void ServerReceiver__Vote(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {

        var voteid = (reader.GetUShort());
        var votenumber = (reader.GetByte());
        if ( ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) ) {
            if (Server_ActiveVote != null) {
                if (Server_ActiveVote.voters.Contains(plr)) {
                    if (!Server_ActiveVote.votedPositive.Contains(plr) && !Server_ActiveVote.votedNegative.Contains(plr)) { // Didn't vote before

                        if (votenumber == 0) {
                            Server_ActiveVote.votedPositive.Add(plr);
                        } else {
                            Server_ActiveVote.votedNegative.Add(plr);
                        }

                    }
                }
            }
        }

    }

    void Update() {
        

        
        if (Net.IsServer && Server_ActiveVote != null) {
            Server_ActiveVote.time -= Time.deltaTime;

            // remove players that may have disconnected
            Server_ActiveVote.voters.RemoveWhere(x => x == null); 
            Server_ActiveVote.votedPositive.RemoveWhere(x => x == null);
            Server_ActiveVote.votedNegative.RemoveWhere(x => x == null);

            HashSet<ScavPlayer> ignorers = new HashSet<ScavPlayer>(Server_ActiveVote.voters);
            ignorers.ExceptWith(Server_ActiveVote.votedPositive);
            ignorers.ExceptWith(Server_ActiveVote.votedNegative);

            if (Server_ActiveVote.time < 0 || ignorers.Count == 0) {
                try {
                    Server_ActiveVote.endAction.Invoke(Server_ActiveVote.votedPositive, Server_ActiveVote.votedNegative, ignorers);
                } catch     (Exception e) {
                    log.error(e);
                }   
                Server_ActiveVote = null;
            } else {

            }


        }
    }







    /*
     
    static TwoWayDictionary<uint, string> _RequestHandlerOriginalNames = new();
    static Dictionary<uint, Action<ScavPlayer, NetDataReader, NetDataWriter>> _ServerRequestHandlers = new();
    static Dictionary<uint, Action<NetDataReader, NetDataWriter>> _ClientRequestHandlers = new();

    public static void Server_Request(ScavPlayer plr, NetDataWriter writer, Action<NetDataReader> action) {
        var writer = Net.CreateWriter((byte)NetmsgId.___1);
        writer.Put(plr.playerId);


    }

    [ClientReceiver((byte)NetmsgId.___1, false)]
    static void ClientReceiver__AnnounceVote(knetid _, LiteNetLib.Utils.NetDataReader reader) {

        var voteid = (reader.GetUShort());
        var title = (reader.GetString());
        var message = (reader.GetString());
        var timetovote = (reader.GetFloat());

    }
     */





}












