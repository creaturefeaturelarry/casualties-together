using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using Together;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Together.UI.TeammateList {
    internal sealed class Roster {
        private const int PageSize = 4;

        private const float StepGap = 0.5f;

        private const float FullBlood = 100f;

        // lost connection uses 3x wound-open distance; click-to-open still uses 1x.
        private const float RangeMult = 3f;

        private const string LostConnection = "Lost Connection";

        private const string ChipNotDetected = "Chip Not Detected";

        private const string Terminated = "TERMINATED";

        private readonly List<Slot> _slots = new List<Slot>(8);

        private bool _wasInGame;

        private readonly Dictionary<ScavPlayer, int> _index = new Dictionary<ScavPlayer, int>(8);

        private readonly Dictionary<Body, int> _byBody = new Dictionary<Body, int>(8);

        private readonly Rect[] _hit = (Rect[])(object)new Rect[4];

        private int _rosterCount = -1;

        private float _nextRoster;

        private int _gen;

        private int _high;

        private int _page;

        private const string CollapsedPref = "Together_TeammateListCollapsed";

        private bool _collapsed = PlayerPrefs.GetInt(CollapsedPref, 0) != 0;

        private int _rr;

        private int _step;

        private int _dragItemId;

        private int _dragSlot = -1;

        private float _nextStep;

        private Slot _job;

        private Rect _panel;

        private string _pageLabel = "";

        private int _pageLabelKey = -1;

        private Texture2D _px;

        private Texture2D _frame;

        private int _frameW;

        private int _frameH;

        private GUIStyle _left;

        private GUIStyle _right;

        private GUIStyle _digit;

        private GUIStyle _small;

        private GUIStyle _smallRight;

        private int _styleH = -1;

        private int _fontSize = -1;

        private Font _font;

        private GameObject _meterObj;

        private RectTransform _meterRt;

        private Canvas _meterCanvas;

        private Vector3[] _corners;

        private static FieldInfo _gameFontField;

        private static bool _gameFontReady;

        private static FieldInfo _lineField;

        private static bool _lineReady;

        private static FieldInfo _colorField;

        private static bool _colorReady;

        public void Tick() {
            bool inGame = Multiplayer.IsNetworkActiveAndIsInGame();
            if (inGame && !_wasInGame) {
                TeammateListHost.NoteLayerReady();
            }
            _wasInGame = inGame;
            if (!inGame) {
                return;
            }

            ReconcileIfChanged();
            RefreshPageRange();
            if (!Typing() && Bind.Pressed()) {
                _collapsed = !_collapsed;
                PlayerPrefs.SetInt(CollapsedPref, _collapsed ? 1 : 0);
                PlayerPrefs.Save();
            }
            if (_high != 0) {
                if (!Covered()) {
                    if (!Typing()) {
                        Hotkeys();
                    }
                    if (!_collapsed) {
                        Drag();
                    }
                }
                StepJob();
            }
        }

        public void NoteTargetUnchipped(ScavPlayer plr, bool unchipped) {
            if (plr == null || !_index.TryGetValue(plr, out int value) || (uint)value >= (uint)_slots.Count) {
                return;
            }
            _slots[value].TargetUnchipped = unchipped;
        }

        public void ResampleChipFlags() {
            TeammateListHost.RefreshViewerChip();
            for (int i = 0; i < _high; i++) {
                Slot slot = _slots[i];
                if (slot?.Player == null) {
                    continue;
                }
                slot.TargetUnchipped = slot.Player.IsUnchipped();
            }
        }

        public void NoteYap(Body body, string line) {
            if ((Object)(object)body == (Object)null || string.IsNullOrEmpty(line) || !_byBody.TryGetValue(body, out int value) || (uint)value >= (uint)_slots.Count) {
                return;
            }
            Slot slot = _slots[value];
            if (!((Object)(object)slot.Player == (Object)null)) {
                string text = Cap(line);
                if (slot.Yap != text) {
                    slot.Yap = text;
                }
            }
        }

        public void Draw() {
            Event current = Event.current;
            if (current == null || _collapsed || _high == 0 || Covered()) {
                _panel = default(Rect);
                for (int i = 0; i < 4; i++) {
                    _hit[i] = default(Rect);
                }
                return;
            }
            if (current.type == EventType.MouseDown && current.button == 1) {
                RightClick(current);
                return;
            }
            // layout and mouse-move events repeat this whole draw. only repaint submits it.
            // hit rects stay from the last repaint so a drag between events still finds a slot.
            bool scroll = current.type == EventType.ScrollWheel;
            bool repaint = current.type == EventType.Repaint;
            if (!scroll && !repaint) {
                return;
            }
            float num = (float)Screen.height / 1080f;
            if (num < 0.75f) {
                num = 0.75f;
            }
            EnsureStyles(num);
            int num2 = (_high + 4 - 1) / 4;
            if (_page >= num2) {
                _page = num2 - 1;
            }
            if (_page < 0) {
                _page = 0;
            }
            float num3 = 360f * num;
            float num4 = 112f * num;
            float num5 = 8f * num;
            float num6 = 4f * num4 + 3f * num5;
            float num7 = MeterLeft() - num3 - 3f;
            float num8 = ((float)Screen.height - num6) * 0.5f;
            _panel = new Rect(num7, num8, num3, num6);
            if (num2 > 1 && scroll && (_panel).Contains(current.mousePosition)) {
                if (current.delta.y > 0f) {
                    _page++;
                } else if (current.delta.y < 0f) {
                    _page--;
                }
                if (_page < 0) {
                    _page = 0;
                }
                if (_page >= num2) {
                    _page = num2 - 1;
                }
                current.Use();
            }
            if (!repaint) {
                return;
            }
            float hintH = 14f * num;
            Rect hint = new Rect(num7, num8 - hintH - 2f * num, num3, hintH);
            if (num2 > 1) {
                int num9 = _page * 16 + num2;
                if (num9 != _pageLabelKey) {
                    _pageLabelKey = num9;
                    _pageLabel = _page + 1 + "/" + num2;
                }
                GUI.Label(new Rect(hint.x, hint.y, hint.width * 0.4f, hint.height), _pageLabel, _small);
            }
            GUI.Label(hint, Bind.HideText(), _smallRight);
            int num10 = _page * 4;
            Rect val = default(Rect);
            for (int j = 0; j < 4; j++) {
                val = new Rect(num7, num8 + (float)j * (num4 + num5), num3, num4);
                _hit[j] = val;
                Slot slot = ((num10 + j < _high) ? _slots[num10 + j] : null);
                bool flag = slot != null && (Object)(object)slot.Player != (Object)null;
                DrawSlot(val, num, j + 1, flag ? slot : null);
            }
        }

        // same 1-4 printed on that player's card
        internal bool TrySlotDigit(ScavPlayer plr, out int digit) {
            digit = 0;
            if ((Object)(object)plr == (Object)null || !_index.TryGetValue(plr, out int index) || index < 0) {
                return false;
            }
            digit = (index % 4) + 1;
            return true;
        }

        static bool Covered() {
            if (Util.IsInTradingMenu()) {
                return true;
            }
            WorldGeneration world = WorldGeneration.world;
            return world != null && world.cutsceneActive;
        }

        private void RightClick(Event current) {
            Vector2 mouse = current.mousePosition;
            int pageStart = _page * 4;
            for (int i = 0; i < 4; i++) {
                if (_hit[i].width <= 0f || !_hit[i].Contains(mouse)) {
                    continue;
                }
                int index = pageStart + i;
                if ((uint)index >= (uint)_slots.Count) {
                    return;
                }
                Slot slot = _slots[index];
                ScavPlayer plr = slot != null ? slot.Player : null;
                if ((Object)(object)plr == (Object)null || (Object)(object)plr.body == (Object)null) {
                    return;
                }
                NetBody nb = plr.netBody;
                if ((Object)(object)nb == (Object)null || (Object)(object)nb.body == (Object)null) {
                    return;
                }
                if (UIInGame.interaction_menu_target_body != nb) {
                    UIInGame.StartPlayerInteractionMenu(nb);
                } else {
                    UIInGame.StopPlayerInteractionMenu();
                }
                current.Use();
                return;
            }
        }

        private void Hotkeys() {
            if (Input.GetKey((KeyCode)304) || Input.GetKey((KeyCode)303)) {
                int num = -1;
                if (Input.GetKeyDown((KeyCode)49) || Input.GetKeyDown((KeyCode)257)) {
                    num = 0;
                } else if (Input.GetKeyDown((KeyCode)50) || Input.GetKeyDown((KeyCode)258)) {
                    num = 1;
                } else if (Input.GetKeyDown((KeyCode)51) || Input.GetKeyDown((KeyCode)259)) {
                    num = 2;
                } else if (Input.GetKeyDown((KeyCode)52) || Input.GetKeyDown((KeyCode)260)) {
                    num = 3;
                }
                if (num >= 0) {
                    OpenAt(_page * 4 + num);
                }
            }
        }

        private void Drag() {
            PlayerCamera main = PlayerCamera.main;
            if ((Object)(object)main == (Object)null) {
                return;
            }
            Item dragItem = main.dragItem;
            if ((Object)(object)dragItem == (Object)null) {
                _dragItemId = 0;
                _dragSlot = -1;
                return;
            }
            Vector2 val = default(Vector2);
            val = new Vector2(Input.mousePosition.x, (float)Screen.height - Input.mousePosition.y);
            int num = -1;
            for (int i = 0; i < 4; i++) {
                if ((_hit[i]).width > 0f && (_hit[i]).Contains(val)) {
                    num = _page * 4 + i;
                    break;
                }
            }
            if (num >= 0) {
                int instanceID = ((Object)dragItem).GetInstanceID();
                if (_dragItemId != instanceID || _dragSlot != num) {
                    _dragItemId = instanceID;
                    _dragSlot = num;
                    OpenAt(num);
                }
            }
        }

        private void OpenAt(int index) {
            if ((uint)index >= (uint)_slots.Count) {
                return;
            }
            Slot slot = _slots[index];
            if ((Object)(object)slot.Player == (Object)null || (Object)(object)slot.Body == (Object)null || PauseHandler.paused) {
                return;
            }
            Body localOpen = Util.GetLocalBody();
            if ((Object)(object)localOpen == (Object)null
                || (Object)(object)slot.Body == (Object)null
                || !KM.DistanceCheck(localOpen, slot.Body, SharedMain.max_player_interaction_distance)) {
                return;
            }
            PlayerCamera main = PlayerCamera.main;
            if ((Object)(object)main == (Object)null) {
                return;
            }
            main.radialOpen = false;
            if ((Object)(object)main.craftingPanel != (Object)null && main.craftingPanel.activeSelf) {
                main.OpenCraftScreen();
            }
            if ((Object)(object)main.tradeMenu != (Object)null && main.tradeMenu.activeSelf) {
                main.ToggleTradeMenu();
            }
            if ((Object)(object)main.currentContainer != (Object)null || ((Object)(object)main.containerMenu != (Object)null && main.containerMenu.activeSelf)) {
                main.CloseContainer();
            }
            MinigameBase main2 = MinigameBase.main;
            if ((Object)(object)main2 != (Object)null && main2.currentMinigame != null && main2.currentMinigame.CanExit()) {
                main2.EndMinigame();
            }
            if (PlayerCamera_ToggleWoundView_MultiplayerPatch.IsOpen()) {
                WoundView view = WoundView.view;
                if ((Object)(object)view == (Object)null || (Object)(object)view.body != (Object)(object)slot.Body) {
                    main.ToggleWoundView(false);
                }
            }
            PlayerCamera_ToggleWoundView_MultiplayerPatch.OpenSpecificBody(slot.Body, true);
        }

        private void StepJob() {
            if (_step == 0) {
                if (!(Time.unscaledTime < _nextStep)) {
                    _nextStep = Time.unscaledTime + 0.5f;
                    _job = NextOccupied();
                    if (_job != null) {
                        _step = 1;
                    }
                }
                return;
            }
            Slot job = _job;
            _job = null;
            int step = _step;
            _step = 0;
            if (job == null || (Object)(object)job.Player == (Object)null) {
                return;
            }
            BindBody(job);
            if (!((Object)(object)job.Body == (Object)null)) {
                switch (step) {
                    case 1:
                    ReadBlood(job);
                    ReadDanger(job);
                    _job = job;
                    _step = 2;
                    break;
                    case 2:
                    ReadVitals(job);
                    _job = job;
                    _step = 3;
                    break;
                    case 3:
                    ReadMood(job);
                    _job = job;
                    _step = 4;
                    break;
                    default:
                    ReadYap(job);
                    break;
                }
            }
        }

        private Slot NextOccupied() {
            int high = _high;
            if (high == 0) {
                return null;
            }
            int num = _rr;
            if (num >= high) {
                num = 0;
            }
            for (int i = 0; i < high; i++) {
                int num2 = num + i;
                if (num2 >= high) {
                    num2 -= high;
                }
                Slot slot = _slots[num2];
                if (!((Object)(object)slot.Player == (Object)null)) {
                    _rr = num2 + 1;
                    return slot;
                }
            }
            return null;
        }

        private void ReadBlood(Slot slot) {
            Body body = slot.Body;
            string text = slot.Player.playerName;
            if (text == null) {
                text = "";
            }
            if (slot.Name != text) {
                slot.Name = text;
            }
            if (!slot.ChipReadable) {
                if (slot.Blood != ChipNotDetected) {
                    slot.Blood = ChipNotDetected;
                }
                return;
            }
            float scale = body.bloodScale;
            float value = body.bloodToLitersBody(body.bloodVolume) * scale;
            float value2 = body.bloodToLitersBody(FullBlood) * scale;
            string text2 = "Blood: " + FormatLiters(value) + "L/" + FormatLiters(value2) + "L";
            if (slot.Blood != text2) {
                slot.Blood = text2;
            }
        }

        private void ReadDanger(Slot slot) {
            Body body = slot.Body;
            // expie full blood is 5L. tint starts there and is solid at 60% (3L).
            // bleed is solid at 1L/min, a fifth of that full volume each minute.
            // milky blood is 0.8x (4.0L, solid at 2.4L, bleed 0.8L/min).
            // dune blood is 1.1x (5.5L, solid at 3.3L, bleed 1.1L/min).
            // brain 60%, oxygen 50%, mood -60, pain 100, satiety 15%, sepsis 35,
            // heart 60-0 and 140-200. those scales are shared, so the same numbers fit all three.
            float scale = body.bloodScale;
            float full = 5f * scale;
            float liters = body.bloodToLitersBody(body.bloodVolume) * scale;
            float bleed = body.bloodToLiters(body.totalBleedSpeed * scale) * 60f;
            float satiety = (body.hunger + body.thirst) * 0.5f;
            float heartRate = body.heartRate;
            float danger = Mathf.InverseLerp(full, full * 0.6f, liters);
            danger = Mathf.Max(danger, Mathf.InverseLerp(0f, full * 0.2f, bleed));
            danger = Mathf.Max(danger, Mathf.InverseLerp(100f, 60f, body.brainHealth));
            danger = Mathf.Max(danger, Mathf.InverseLerp(100f, 50f, body.bloodOxygen));
            danger = Mathf.Max(danger, Mathf.InverseLerp(0f, -60f, body.totalHappiness));
            danger = Mathf.Max(danger, Mathf.InverseLerp(0f, 100f, body.averagePain));
            danger = Mathf.Max(danger, Mathf.InverseLerp(100f, 15f, satiety));
            danger = Mathf.Max(danger, Mathf.InverseLerp(0f, 35f, body.septicShock));
            danger = Mathf.Max(danger, Mathf.InverseLerp(60f, 0f, heartRate));
            danger = Mathf.Max(danger, Mathf.InverseLerp(140f, 200f, heartRate));
            slot.Danger = danger;
        }

        private void ReadVitals(Slot slot) {
            Body body = slot.Body;
            if (!slot.ChipReadable) {
                if (slot.Mind != ChipNotDetected) {
                    slot.Mind = ChipNotDetected;
                }
            } else {
                string text = "Consciousness: " + Mathf.RoundToInt(body.consciousness) + "%";
                if (slot.Mind != text) {
                    slot.Mind = text;
                }
            }
            string text2 = "Satiety: " + Mathf.RoundToInt((body.hunger + body.thirst) * 0.5f);
            if (slot.Satiety != text2) {
                slot.Satiety = text2;
            }
        }

        private void ReadMood(Slot slot) {
            if (!slot.ChipReadable) {
                if (slot.Mood != ChipNotDetected) {
                    slot.Mood = ChipNotDetected;
                }
            } else {
                string text = "Mood: " + Mathf.RoundToInt(slot.Body.totalHappiness) + "%";
                if (slot.Mood != text) {
                    slot.Mood = text;
                }
            }
            FillMoodles(slot);
        }

        private void ReadYap(Slot slot) {
            Talker talker = slot.Body.talker;
            if (!((Object)(object)talker == (Object)null)) {
                NoteYap(slot.Body, CurrentLine(talker));
            }
        }

        private void FillMoodles(Slot slot) {
            slot.MoodleCount = 0;
            Body body = slot.Body;
            MoodleManager main = MoodleManager.main;
            if ((Object)(object)body == (Object)null || !body.alive || (Object)(object)main == (Object)null || main.icons == null || (Object)(object)WorldGeneration.world == (Object)null) {
                return;
            }
            if (body.focusedLevel > 0f) {
                Push(slot, main, 8, "focused");
            } else if (body.horrifiedLevel > 0f) {
                Push(slot, main, (body.horrifiedLevel <= 50f) ? 1 : 3, (body.horrifiedLevel <= 50f) ? "impendingdoom" : "horrified");
            }
            if (TeammateListHost.ViewerUnchipped && !body.inCardiacArrest) {
                if (body.heartRate > 110f) {
                    Push(slot, main, (body.heartRate > 160f) ? ((!(body.heartRate > 200f)) ? 1 : 2) : 0, "tachycardia");
                } else if (body.heartRate < 60f) {
                    Push(slot, main, (body.heartRate < 40f) ? 1 : 0, "bradycardia");
                }
                if (body.fibrillationProgress > 50f || body.heartRate > 240f) {
                    Push(slot, main, 3, "palpitations");
                }
            }
            if (body.lastStandTime > 0f) {
                Push(slot, main, 8, "lastleg");
            }
            if (body.brainHealth < 30f) {
                Push(slot, main, 3, "braindamage", chippedOnly: true);
            } else if (body.brainHealth < 60f) {
                Push(slot, main, 2, "braindamage", chippedOnly: true);
            } else if (body.brainHealth < 80f) {
                Push(slot, main, 1, "braindamage", chippedOnly: true);
            } else if (body.brainHealth < 95f) {
                Push(slot, main, 0, "braindamage", chippedOnly: true);
            }
            if (body.strokeAmount > 70f) {
                Push(slot, main, 3, "stroke", chippedOnly: true);
            }
            if (body.inCardiacArrest) {
                Push(slot, main, 3, "cardiacarrest");
            } else if (body.fibrillationProgress > 75f) {
                Push(slot, main, 3, "arrythmia", chippedOnly: true);
            } else if (body.fibrillationProgress > 50f) {
                Push(slot, main, 2, "arrythmia", chippedOnly: true);
            } else if (body.fibrillationProgress > 15f) {
                Push(slot, main, 1, "arrythmia", chippedOnly: true);
            }
            if (!body.inCardiacArrest) {
                if (body.bloodPressure < 60f) {
                    Push(slot, main, 3, "hypotension", chippedOnly: true);
                } else if (body.bloodPressure < 83f) {
                    Push(slot, main, 2, "hypotension", chippedOnly: true);
                } else if (body.bloodPressure < 96f) {
                    Push(slot, main, 1, "hypotension", chippedOnly: true);
                } else if (body.bloodPressure < 110f) {
                    Push(slot, main, 0, "hypotension", chippedOnly: true);
                }
                if (body.bloodPressure > 180f) {
                    Push(slot, main, 3, "hypertension", chippedOnly: true);
                } else if (body.bloodPressure > 162f) {
                    Push(slot, main, 2, "hypertension", chippedOnly: true);
                } else if (body.bloodPressure > 145f) {
                    Push(slot, main, 1, "hypertension", chippedOnly: true);
                } else if (body.bloodPressure > 130f) {
                    Push(slot, main, 0, "hypertension", chippedOnly: true);
                }
            }
            if (!body.breathing) {
                Push(slot, main, 3, "cantbreathe");
            } else if (body.respiratoryRate < 50f) {
                Push(slot, main, 1, "hypoventilation");
            } else if (body.respiratoryRate < 90f) {
                Push(slot, main, 0, "hypoventilation");
            }
            Limb[] limbs = body.limbs;
            if (limbs != null && limbs.Length > 1 && (Object)(object)limbs[1] != (Object)null && limbs[1].muscleHealth < 4f) {
                Push(slot, main, 3, "thoraxdestroyed");
            }
            if (body.hemothorax > 40f) {
                Push(slot, main, 1, "hemothorax", chippedOnly: true);
            }
            if (body.hasPulmonaryEmbolism) {
                Push(slot, main, 3, "pulmonaryembolism", chippedOnly: true);
            }
            if (body.bloodOxygen < 45f) {
                Push(slot, main, 3, "oxygen", chippedOnly: true);
            } else if (body.bloodOxygen < 60f) {
                Push(slot, main, 2, "oxygen", chippedOnly: true);
            } else if (body.bloodOxygen < 75f) {
                Push(slot, main, 1, "oxygen", chippedOnly: true);
            } else if (body.bloodOxygen < 90f) {
                Push(slot, main, 0, "oxygen", chippedOnly: true);
            }
            if (body.radiationSickness > 80f) {
                Push(slot, main, 3, "irradiated", chippedOnly: true);
            } else if (body.radiationSickness > 50f) {
                Push(slot, main, 2, "irradiated", chippedOnly: true);
            } else if (body.radiationSickness > 30f) {
                Push(slot, main, 1, "irradiated", chippedOnly: true);
            } else if (body.radiationSickness > 10f) {
                Push(slot, main, 0, "irradiated", chippedOnly: true);
            }
            if (body.averagePain > 80f) {
                Push(slot, main, 3, "pain");
            } else if (body.averagePain > 55f) {
                Push(slot, main, 2, "pain");
            } else if (body.averagePain > 30f) {
                Push(slot, main, 1, "pain");
            } else if (body.averagePain > 10f) {
                Push(slot, main, 0, "pain");
            }
            if (body.painShock > 0.3f) {
                Push(slot, main, 3, "shock");
            }
            Painkillers val = default(Painkillers);
            if (((Component)body).TryGetComponent<Painkillers>(out val)) {
                float actualOpiateReception = val.actualOpiateReception;
                if (actualOpiateReception > 80f) {
                    Push(slot, main, 3, "overdose", chippedOnly: true);
                } else if (actualOpiateReception > 50f) {
                    Push(slot, main, 2, "overdose", chippedOnly: true);
                } else if (actualOpiateReception > 20f) {
                    Push(slot, main, 1, "overdose", chippedOnly: true);
                } else if (actualOpiateReception > 5f) {
                    Push(slot, main, 0, "overdose", chippedOnly: true);
                }
                if (actualOpiateReception < -34f) {
                    Push(slot, main, 3, "withdrawal", chippedOnly: true);
                } else if (actualOpiateReception < -25f) {
                    Push(slot, main, 2, "withdrawal", chippedOnly: true);
                } else if (actualOpiateReception < -15f) {
                    Push(slot, main, 1, "withdrawal", chippedOnly: true);
                } else if (actualOpiateReception < -5f) {
                    Push(slot, main, 0, "withdrawal", chippedOnly: true);
                }
            }
            if (body.onHardStimulants) {
                Push(slot, main, 6, "stimulants", chippedOnly: true);
            }
            if (limbs != null && limbs.Length != 0 && (Object)(object)limbs[0] != (Object)null && limbs[0].muscleHealth < 14.4f) {
                Push(slot, main, 3, "concussion", chippedOnly: true);
            } else if (body.consciousness <= 20f) {
                Push(slot, main, body.sleeping ? 4 : 3, body.sleeping ? "asleep" : "unconscious");
            } else if (body.consciousness < 30f) {
                Push(slot, main, 3, "confused");
            } else if (body.consciousness < 55f) {
                Push(slot, main, 2, "confused");
            } else if (body.consciousness < 72f) {
                Push(slot, main, 1, "confused");
            } else if (body.consciousness < 90f) {
                Push(slot, main, 0, "confused");
            }
            if (body.overdoseIndex > 0) {
                Push(slot, main, 3, "drugoverdose", chippedOnly: true);
            }
            if (body.internalBleeding > 5f) {
                Push(slot, main, (int)((body.internalBleeding - 1f) / 25f), "internalBleed", chippedOnly: true);
            }
            if (body.totalBleedSpeed > 0.3f) {
                Push(slot, main, 3, "maxbleeding");
            } else if (body.totalBleedSpeed > 0.15f) {
                Push(slot, main, 2, "heavybleeding");
            } else if (body.totalBleedSpeed > 0.06f) {
                Push(slot, main, 1, "bleeding");
            } else if (body.totalBleedSpeed > 0f) {
                Push(slot, main, 0, "smallbleeding");
            }
            if (body.stamina <= 15f) {
                Push(slot, main, 3, "exertion");
            } else if (body.stamina < 35f) {
                Push(slot, main, 2, "exertion");
            } else if (body.stamina < 50f) {
                Push(slot, main, 1, "exertion");
            } else if (body.stamina < 70f) {
                Push(slot, main, 0, "exertion");
            }
            float num = 0f;
            float num2 = 0f;
            float num3 = 0f;
            if (limbs != null) {
                foreach (Limb val2 in limbs) {
                    if (!((Object)(object)val2 == (Object)null) && !val2.dismembered) {
                        if (val2.infectionAmount > num) {
                            num = val2.infectionAmount;
                        }
                        if (val2.broken && val2.boneHealTimer > num2) {
                            num2 = val2.boneHealTimer;
                        }
                        if (val2.dislocated && val2.dislocationTimer > num3) {
                            num3 = val2.dislocationTimer;
                        }
                    }
                }
            }
            if (num2 > 0f) {
                Push(slot, main, Mathf.RoundToInt(num2 * 0.03f), "brokenbone", chippedOnly: true);
            }
            if (num3 > 0f) {
                Push(slot, main, Mathf.RoundToInt(num3 * 0.03f), "dislocation", chippedOnly: true);
            }
            if (limbs != null && limbs.Length > 1 && (Object)(object)limbs[0] != (Object)null && limbs[0].broken) {
                Push(slot, main, Mathf.RoundToInt(limbs[0].boneHealTimer * 0.03f), "brokenneck", chippedOnly: true);
            }
            if (limbs != null && limbs.Length > 1 && (Object)(object)limbs[1] != (Object)null && limbs[1].broken) {
                Push(slot, main, Mathf.RoundToInt(limbs[1].boneHealTimer * 0.03f), "brokenribs", chippedOnly: true);
            }
            if (limbs != null && limbs.Length > 1 && (Object)(object)limbs[0] != (Object)null && limbs[0].dislocated) {
                Push(slot, main, Mathf.RoundToInt(limbs[0].dislocationTimer * 0.03f), "dislocatedjaw", chippedOnly: true);
            }
            if (limbs != null && limbs.Length > 1 && (Object)(object)limbs[1] != (Object)null && limbs[1].dislocated) {
                Push(slot, main, Mathf.RoundToInt(limbs[1].dislocationTimer * 0.03f), "dislocatedspine", chippedOnly: true);
            }
            if (num > 80f) {
                Push(slot, main, 3, "infected");
            } else if (num > 60f) {
                Push(slot, main, 2, "infected");
            } else if (num > 40f) {
                Push(slot, main, 1, "infected");
            } else if (num > 25f) {
                Push(slot, main, 0, "infected", chippedOnly: true);
            }
            if (body.septicShock > 80f) {
                Push(slot, main, 3, "sepsis", chippedOnly: true);
            } else if (body.septicShock > 50f) {
                Push(slot, main, 2, "sepsis", chippedOnly: true);
            } else if (body.septicShock > 10f) {
                Push(slot, main, 1, "sepsis", chippedOnly: true);
            }
            if (body.venomCurrent > 90f) {
                Push(slot, main, 3, "venom");
            } else if (body.venomCurrent > 55f) {
                Push(slot, main, 2, "venom");
            } else if (body.venomCurrent > 25f) {
                Push(slot, main, 1, "venom");
            } else if (body.venomCurrent > 2f) {
                Push(slot, main, 0, "venom");
            }
            if (body.energy <= 7f) {
                Push(slot, main, 3, "tired");
            } else if (body.energy < 15f) {
                Push(slot, main, 2, "tired");
            } else if (body.energy < 25f) {
                Push(slot, main, 1, "tired");
            } else if (body.energy < 35f) {
                Push(slot, main, 0, "tired");
            }
            float num4 = Mathf.Clamp01(1f + body.happiness * 0.009f);
            if (body.hunger <= 15f * num4) {
                Push(slot, main, 3, "hunger");
            } else if (body.hunger < 35f * num4) {
                Push(slot, main, 2, "hunger");
            } else if (body.hunger < 50f * num4) {
                Push(slot, main, 1, "hunger");
            } else if (body.hunger < 75f * num4) {
                Push(slot, main, 0, "hunger");
            }
            if (body.hunger > 120f) {
                Push(slot, main, 5, "hunger");
            } else if (body.hunger > 100f) {
                Push(slot, main, 4, "hunger");
            }
            if (body.thirst <= 20f) {
                Push(slot, main, 3, "thirst");
            } else if (body.thirst < 35f) {
                Push(slot, main, 2, "thirst");
            } else if (body.thirst < 55f) {
                Push(slot, main, 1, "thirst");
            } else if (body.thirst < 75f) {
                Push(slot, main, 0, "thirst");
            }
            if (body.thirst > 175f) {
                Push(slot, main, 3, "overhydrated");
            } else if (body.thirst > 125f) {
                Push(slot, main, 1, "overhydrated");
            } else if (body.thirst > 100f) {
                Push(slot, main, 0, "overhydrated");
            }
            if (body.sicknessAmount > 75f) {
                Push(slot, main, 3, "sick");
            } else if (body.sicknessAmount > 50f) {
                Push(slot, main, 2, "sick");
            } else if (body.sicknessAmount > 30f) {
                Push(slot, main, 1, "sick");
            } else if (body.sicknessAmount > 10f) {
                Push(slot, main, 0, "sick");
            }
            if (body.temperature > 41.5f) {
                Push(slot, main, 3, "hot");
            } else if (body.temperature > 40.25f) {
                Push(slot, main, 2, "hot");
            } else if (body.temperature > 39f) {
                Push(slot, main, 1, "hot");
            } else if (body.temperature > 38f) {
                Push(slot, main, 0, "hot");
            }
            if (body.temperature < 28f) {
                Push(slot, main, 3, "cold");
            } else if (body.temperature < 32.5f) {
                Push(slot, main, 2, "cold");
            } else if (body.temperature < 34f) {
                Push(slot, main, 1, "cold");
            } else if (body.temperature < 35.5f) {
                Push(slot, main, 0, "cold");
            }
            if (body.totalHappiness <= -75f) {
                Push(slot, main, 3, "miserable");
            } else if (body.totalHappiness < -50f) {
                Push(slot, main, 2, "depression");
            } else if (body.totalHappiness < -30f) {
                Push(slot, main, 1, "gloomy");
            } else if (body.totalHappiness < -10f) {
                Push(slot, main, 0, "sad");
            }
            if (body.totalHappiness >= 75f) {
                Push(slot, main, 7, "happy");
            } else if (body.totalHappiness > 50f) {
                Push(slot, main, 6, "happy");
            } else if (body.totalHappiness > 30f) {
                Push(slot, main, 5, "happy");
            } else if (body.totalHappiness > 10f) {
                Push(slot, main, 4, "happy");
            }
            if (body.clawHealth < 25f) {
                Push(slot, main, 1, "clawdamage");
            } else if (body.clawHealth < 60f) {
                Push(slot, main, 0, "clawdamage");
            }
            if (body.hearingLoss > 70f) {
                Push(slot, main, 2, "hearingloss");
            } else if (body.hearingLoss > 40f) {
                Push(slot, main, 1, "hearingloss");
            } else if (body.hearingLoss > 15f) {
                Push(slot, main, 0, "hearingloss");
            }
            if (body.dirtyness > 80f) {
                Push(slot, main, 1, "dirty");
            } else if (body.dirtyness > 50f) {
                Push(slot, main, 0, "dirty");
            }
            if (body.overEncumberance > 0.85f) {
                Push(slot, main, 3, "encumbered");
            } else if (body.overEncumberance > 0.55f) {
                Push(slot, main, 2, "encumbered");
            } else if (body.overEncumberance > 0.3f) {
                Push(slot, main, 1, "encumbered");
            } else if (body.overEncumberance > 0f) {
                Push(slot, main, 0, "encumbered");
            }
            if (body.wetness > 70f) {
                Push(slot, main, 3, "wet");
            } else if (body.wetness > 45f) {
                Push(slot, main, 2, "wet");
            } else if (body.wetness > 20f) {
                Push(slot, main, 1, "wet");
            } else if (body.wetness > 5f) {
                Push(slot, main, 0, "wet");
            }
        }

        private static void Push(Slot slot, MoodleManager mgr, int intensity, string icon, bool chippedOnly = false) {
            if (slot.MoodleCount >= 10 || (chippedOnly && !slot.ChipReadable) || !mgr.icons.TryGetValue(icon, out Sprite value) || (Object)(object)value == (Object)null) {
                return;
            }
            Sprite val = null;
            Sprite[] backgroundIcons = mgr.backgroundIcons;
            if (backgroundIcons != null && backgroundIcons.Length != 0) {
                if (intensity < 0) {
                    intensity = 0;
                }
                if (intensity >= backgroundIcons.Length) {
                    intensity = backgroundIcons.Length - 1;
                }
                val = backgroundIcons[intensity];
            }
            int moodleCount = slot.MoodleCount;
            slot.MoodleBack[moodleCount] = val;
            slot.MoodleIcon[moodleCount] = value;
            slot.MoodleCount = moodleCount + 1;
        }

        private static Font GameFont() {
            if (!_gameFontReady) {
                _gameFontReady = true;
                Type type = AccessTools.TypeByName("Together.CoopModAssets")
                    ?? AccessTools.TypeByName("KrokoshaCasualtiesMP.CoopModAssets");
                if (type != null) {
                    _gameFontField = type.GetField("gamefont", BindingFlags.Static | BindingFlags.Public);
                }
            }
            if (_gameFontField == null) {
                return null;
            }
            object value = _gameFontField.GetValue(null);
            return (Font)((value is Font) ? value : null);
        }

        private static string FormatLiters(float value) {
            float num = Mathf.Round(value * 10f) / 10f;
            float num2 = Mathf.Round(num);
            if (Mathf.Abs(num - num2) < 0.001f) {
                return num2.ToString("0");
            }
            return num.ToString("0.0");
        }

        public static string CurrentLine(Talker talker) {
            if (!_lineReady) {
                _lineReady = true;
                _lineField = typeof(Talker).GetField("currentString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (_lineField == null || (Object)(object)talker == (Object)null) {
                return null;
            }
            return _lineField.GetValue(talker) as string;
        }

        private static Color NameColor(ScavPlayer plr) {
            if (!_colorReady) {
                _colorReady = true;
                _colorField = typeof(ScavPlayer).GetField("playerColor", BindingFlags.Instance | BindingFlags.Public) ?? typeof(ScavPlayer).GetField("plrcolor", BindingFlags.Instance | BindingFlags.Public);
            }
            if (_colorField == null || (Object)(object)plr == (Object)null) {
                return Color.white;
            }
            object value = _colorField.GetValue(plr);
            if (value is Color24 val) {
                Color result = val;
                result.a = 1f;
                return result;
            }
            if (value is Color result2) {
                result2.a = 1f;
                return result2;
            }
            return Color.white;
        }

        private static string Cap(string line) {
            int length = line.Length;
            int num = 0;
            for (int i = 0; i < length; i++) {
                if (num >= 30) {
                    break;
                }
                if (line[i] == '\n') {
                    break;
                }
                if (line[i] == '\r') {
                    break;
                }
                num++;
            }
            if (num == length) {
                return line;
            }
            return line.Substring(0, num);
        }

        private static bool Typing() {
            if (Chat.CHAT_textbox_input_focused) {
                return true;
            }
            ConsoleScript instance = ConsoleScript.instance;
            if ((Object)(object)instance != (Object)null) {
                return instance.active;
            }
            return false;
        }

        private void ReconcileIfChanged() {
            Dictionary<knetid, ScavPlayer> clientIdToPlayerDict = ScavPlayer.ClientIdToPlayerDict;
            int num = clientIdToPlayerDict?.Count ?? 0;
            float unscaledTime = Time.unscaledTime;
            if (num == _rosterCount && unscaledTime < _nextRoster) {
                return;
            }
            _rosterCount = num;
            _nextRoster = unscaledTime + 2f;
            _gen++;
            if (clientIdToPlayerDict != null) {
                ScavPlayer lOCAL_PLAYER = ScavPlayer.LocalPlayer;
                foreach (KeyValuePair<knetid, ScavPlayer> item in clientIdToPlayerDict) {
                    ScavPlayer value = item.Value;
                    if (!((Object)(object)value == (Object)null) && !((Object)(object)value == (Object)(object)lOCAL_PLAYER)) {
                        Touch(value);
                    }
                }
            }
            DropStale();
        }

        private void BindBody(Slot slot) {
            if (!_index.TryGetValue(slot.Player, out int value)) {
                return;
            }
            Body body = slot.Player.body;
            if (!((Object)(object)slot.Body == (Object)(object)body)) {
                if ((Object)(object)slot.Body != (Object)null) {
                    _byBody.Remove(slot.Body);
                }
                slot.Body = body;
                if ((Object)(object)body != (Object)null) {
                    _byBody[body] = value;
                }
            }
        }

        private void RefreshPageRange() {
            int start = _page * PageSize;
            int end = start + PageSize;
            if (end > _high) {
                end = _high;
            }
            for (int i = start; i < end; i++) {
                Slot slot = _slots[i];
                if (slot != null && (Object)(object)slot.Player != (Object)null) {
                    RefreshSlotRange(slot);
                }
            }
        }

        private static void RefreshSlotRange(Slot slot) {
            Body local = Util.GetLocalBody();
            Body target = slot.Body;
            if ((Object)(object)local == (Object)null || (Object)(object)target == (Object)null) {
                slot.InRange = false;
                return;
            }
            // host rule: the list can keep working no matter how far the teammate is
            if (!Multiplayer.rules.TeammateListUsesRange) {
                slot.InRange = true;
                return;
            }
            slot.InRange = KM.DistanceCheck(local, target, SharedMain.max_player_interaction_distance * RangeMult);
        }

        private void Touch(ScavPlayer plr) {
            Slot slot;
            if (_index.TryGetValue(plr, out int value)) {
                slot = _slots[value];
            } else {
                value = FirstEmpty();
                if (value < 0) {
                    value = _slots.Count;
                    slot = new Slot();
                    _slots.Add(slot);
                } else {
                    slot = _slots[value];
                }
                slot.Player = plr;
                _index[plr] = value;
                if (value + 1 > _high) {
                    _high = value + 1;
                }
            }
            slot.Stamp = _gen;
            slot.NameColor = NameColor(plr);
            BindBody(slot);
            slot.TargetUnchipped = plr.IsUnchipped();
            RefreshSlotRange(slot);
            int species = slot.Body != null ? slot.Body.charType : 0;
            if (slot.Species != species || slot.Head == null) {
                slot.Species = species;
                SpeciesIcon.Assign(slot, species);
            }
            string text = plr.playerName;
            if (text == null) {
                text = "";
            }
            if (slot.Name != text) {
                slot.Name = text;
            }
        }

        private int FirstEmpty() {
            int count = _slots.Count;
            for (int i = 0; i < count; i++) {
                if ((Object)(object)_slots[i].Player == (Object)null) {
                    return i;
                }
            }
            return -1;
        }

        private void DropStale() {
            int count = _slots.Count;
            int high = 0;
            for (int i = 0; i < count; i++) {
                Slot slot = _slots[i];
                if ((Object)(object)slot.Player == (Object)null) {
                    continue;
                }
                if (slot.Stamp == _gen) {
                    high = i + 1;
                    continue;
                }
                _index.Remove(slot.Player);
                if ((Object)(object)slot.Body != (Object)null) {
                    _byBody.Remove(slot.Body);
                }
                slot.Player = null;
                slot.Body = null;
                slot.Name = "";
                slot.Mood = "";
                slot.Yap = "";
                slot.Blood = "";
                slot.Mind = "";
                slot.Satiety = "";
                slot.Danger = 0f;
                slot.MoodleCount = 0;
            }
            _high = high;
            int num = ((_high == 0) ? 1 : ((_high + 4 - 1) / 4));
            if (_page >= num) {
                _page = num - 1;
            }
        }

        private void EnsureStyles(float s) {
            int height = Screen.height;
            Font val = GameFont();
            if ((Object)(object)val == (Object)null && (Object)(object)GUI.skin != (Object)null) {
                val = GUI.skin.font;
            }
            int num = (((Object)(object)val != (Object)null) ? UIBullshit.GetCurFontSize(0.5f) : Mathf.RoundToInt(15f * s));
            if (num < 12) {
                num = 12;
            }
            if (_left == null || _styleH != height || !((Object)(object)_px != (Object)null) || !((Object)(object)_font == (Object)(object)val) || _fontSize != num) {
                _styleH = height;
                _font = val;
                _fontSize = num;
                if ((Object)(object)_px == (Object)null) {
                    _px = new Texture2D(1, 1, (TextureFormat)4, false);
                    _px.SetPixel(0, 0, Color.white);
                    _px.Apply(false, true);
                    ((Object)_px).hideFlags = (HideFlags)61;
                }
                _left = new GUIStyle(GUI.skin.label);
                _left.font = val;
                _left.fontSize = num;
                _left.alignment = (TextAnchor)3;
                _left.wordWrap = false;
                _left.clipping = (TextClipping)1;
                _left.normal.textColor = Color.white;
                _right = new GUIStyle(_left);
                _right.alignment = (TextAnchor)5;
                _digit = new GUIStyle(_left);
                _digit.fontSize = Mathf.Max(12, num - 2);
                _digit.alignment = (TextAnchor)4;
                _digit.normal.textColor = new Color(0.82f, 0.86f, 0.9f, 1f);
                int small = Mathf.Max(10, Mathf.RoundToInt(num * 0.72f));
                _small = new GUIStyle(_left);
                _small.fontSize = small;
                _small.alignment = (TextAnchor)3;
                _small.normal.textColor = _digit.normal.textColor;
                _smallRight = new GUIStyle(_small);
                _smallRight.alignment = (TextAnchor)5;
            }
        }

        private float MeterLeft() {
            PlayerCamera main = PlayerCamera.main;
            GameObject val = (((Object)(object)main != (Object)null) ? main.depthMeter : null);
            if ((Object)(object)val == (Object)null) {
                return Screen.width;
            }
            if ((Object)(object)val != (Object)(object)_meterObj) {
                _meterObj = val;
                _meterRt = val.GetComponent<RectTransform>();
                _meterCanvas = (((Object)(object)_meterRt != (Object)null) ? ((Component)_meterRt).GetComponentInParent<Canvas>() : null);
            }
            if ((Object)(object)_meterRt == (Object)null) {
                return Screen.width;
            }
            if (_corners == null) {
                _corners = (Vector3[])(object)new Vector3[4];
            }
            _meterRt.GetWorldCorners(_corners);
            float num = CornerX(_corners[0]);
            for (int i = 1; i < 4; i++) {
                float num2 = CornerX(_corners[i]);
                if (num2 < num) {
                    num = num2;
                }
            }
            return num;
        }

        private float CornerX(Vector3 world) {
            if ((Object)(object)_meterCanvas == (Object)null || (int)_meterCanvas.renderMode == 0) {
                return world.x;
            }
            Camera val = _meterCanvas.worldCamera;
            if ((Object)(object)val == (Object)null) {
                PlayerCamera main = PlayerCamera.main;
                if ((Object)(object)main != (Object)null) {
                    val = ((Component)main).GetComponent<Camera>();
                }
            }
            if ((Object)(object)val == (Object)null) {
                return world.x;
            }
            return val.WorldToScreenPoint(world).x;
        }

        private void DrawSlot(Rect rect, float s, int digit, Slot slot) {
            Color color = GUI.color;
            bool lost = slot != null && !slot.InRange;
            EnsureFrame(Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height));
            GUI.color = lost ? new Color(0.55f, 0.55f, 0.55f, 1f) : Color.white;
            GUI.DrawTexture(rect, _frame);
            Rect val = Expand(rect, -6f);
            if (lost) {
                GUI.color = new Color(0.35f, 0.35f, 0.35f, 0.55f);
            } else {
                GUI.color = new Color(slot?.Danger ?? 0f, 0f, 0f, 0.25f);
            }
            GUI.DrawTexture(val, (Texture)(object)_px);
            GUI.color = lost ? new Color(0.75f, 0.75f, 0.75f, 1f) : Color.white;
            float num = 4f * s;
            float num5 = ((val).height - num * 2f) / 4f;
            float num6 = (val).y + num;
            float digitW = 18f * s;
            float icon = num5 * 1.2f;
            if (_left != null) {
                float textH = _left.CalcSize(new GUIContent("Ay")).y;
                if (textH >= 1f && textH < num5) {
                    icon = textH * 1.2f;
                }
            }
            GUI.Label(new Rect((val).x, (val).y, digitW, (val).height), digit.ToString(), _digit);
            if (slot == null) {
                GUI.color = color;
                return;
            }
            if (slot.Head == null) {
                SpeciesIcon.Assign(slot, slot.Species);
            }
            float gutter = digitW;
            if (slot.Head != null && icon >= 1f) {
                float iconX = (val).x + digitW;
                float iconY = (val).y + ((val).height - icon) * 0.5f;
                DrawSprite(new Rect(iconX, iconY, icon, icon), slot.Head);
                DrawSprite(new Rect(iconX, iconY, icon, icon), slot.HeadDetail);
                gutter += icon + 2f * s;
            }
            float num2 = (val).x + gutter;
            float num3 = (val).xMax - num2 - 4f * s;
            if (num3 < 1f) {
                num3 = 1f;
            }
            float num4 = num3 * 0.5f;
            bool dead = slot.Body != null && !slot.Body.alive;
            float statusW = _right.CalcSize(new GUIContent(Terminated)).x + 4f * s;
            if (statusW > num3 * 0.55f) {
                statusW = num3 * 0.55f;
            }
            _left.normal.textColor = lost ? new Color(0.75f, 0.75f, 0.75f, 1f) : slot.NameColor;
            Rect nameRect = new Rect(num2, num6, num3 - statusW, num5);
            Label(nameRect, slot.Name, _left);
            if (dead) {
                Strike(nameRect, slot.Name, lost ? new Color(0.75f, 0.75f, 0.75f, 1f) : slot.NameColor, s);
            }
            _right.normal.textColor = lost ? new Color(0.75f, 0.75f, 0.75f, 1f) : _digit.normal.textColor;
            Label(new Rect(num2 + num3 - statusW, num6, statusW, num5), dead ? Terminated : Bind.OpenText(digit), _right);
            _left.normal.textColor = lost ? new Color(0.75f, 0.75f, 0.75f, 1f) : Color.white;
            _right.normal.textColor = _left.normal.textColor;
            if (lost) {
                Label(new Rect(num2, num6 + num5, num3 - 4f, num5), LostConnection, _left);
                Label(new Rect(num2, num6 + num5 * 2f, num3 - 4f, num5), LostConnection, _left);
                Label(new Rect(num2, num6 + num5 * 3f, num3 - 4f, num5), LostConnection, _left);
            } else {
                DrawMoodles(new Rect(num2, num6 + num5, num4 - 4f, num5), slot);
                Label(new Rect(num2 + num4, num6 + num5, num4 - 4f, num5), slot.Blood, _right);
                Label(new Rect(num2, num6 + num5 * 2f, num4 - 4f, num5), slot.Mood, _left);
                _right.clipping = (TextClipping)0;
                Label(new Rect(num2, num6 + num5 * 2f, num3 - 4f, num5), slot.Mind, _right);
                _right.clipping = (TextClipping)1;
                Label(new Rect(num2, num6 + num5 * 3f, num4 - 4f, num5), slot.Yap, _left);
                Label(new Rect(num2 + num4, num6 + num5 * 3f, num4 - 4f, num5), slot.Satiety, _right);
            }
            GUI.color = color;
        }

        private void DrawMoodles(Rect area, Slot slot) {
            int num = slot.MoodleCount;
            if (num <= 0) {
                return;
            }
            if (num > 10) {
                num = 10;
            }
            float num2 = (area).height;
            float num3 = (area).width / (float)num;
            if (num2 > num3) {
                num2 = num3;
            }
            if (!(num2 < 1f)) {
                float num4 = (area).y + ((area).height - num2) * 0.5f;
                // backs share one atlas and icons share another. alternating them was a pass per icon.
                for (int i = 0; i < num; i++) {
                    DrawSprite(new Rect((area).x + (float)i * num2, num4, num2, num2), slot.MoodleBack[i]);
                }
                for (int i = 0; i < num; i++) {
                    DrawSprite(new Rect((area).x + (float)i * num2, num4, num2, num2), slot.MoodleIcon[i]);
                }
            }
        }

        private static void DrawSprite(Rect r, Sprite sprite) {
            if ((Object)(object)sprite == (Object)null) {
                return;
            }
            Texture texture = (Texture)(object)sprite.texture;
            if (!((Object)(object)texture == (Object)null)) {
                Rect textureRect = sprite.textureRect;
                float num = texture.width;
                float num2 = texture.height;
                if (!(num <= 0f) && !(num2 <= 0f)) {
                    GUI.DrawTextureWithTexCoords(r, texture, new Rect((textureRect).x / num, (textureRect).y / num2, (textureRect).width / num, (textureRect).height / num2));
                }
            }
        }

        private static Rect Expand(Rect r, float n) {
            (r).xMin = (r).xMin - n;
            (r).yMin = (r).yMin - n;
            (r).xMax = (r).xMax + n;
            (r).yMax = (r).yMax + n;
            return r;
        }

        private void Strike(Rect row, string text, Color color, float s) {
            if (_px == null || _left == null || string.IsNullOrEmpty(text)) {
                return;
            }
            float width = _left.CalcSize(new GUIContent(text)).x;
            if (width > row.width) {
                width = row.width;
            }
            if (width < 1f) {
                return;
            }
            float thick = Mathf.Max(1f, s);
            float y = row.y + (row.height - thick) * 0.5f;
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(row.x, y, width, thick), _px);
            GUI.color = previous;
        }

        private static void Label(Rect r, string text, GUIStyle style) {
            if (string.IsNullOrEmpty(text)) {
                return;
            }
            GUI.Label(r, text, style);
        }

        private void EnsureFrame(int w, int h) {
            if (w < 8) {
                w = 8;
            }
            if (h < 8) {
                h = 8;
            }
            if ((Object)(object)_frame != (Object)null && _frameW == w && _frameH == h) {
                return;
            }
            if ((Object)(object)_frame != (Object)null) {
                Object.Destroy((Object)(object)_frame);
            }
            Texture2D tex = new Texture2D(w, h, (TextureFormat)4, false);
            Color32[] pixels = new Color32[w * h];
            Color32 white = new Color32(255, 255, 255, 255);
            Color32 gray = new Color32(107, 107, 107, 255);
            Color32 black = new Color32(20, 20, 20, 255);
            Color32 clear = new Color32(0, 0, 0, 0);
            for (int y = 0; y < h; y++) {
                int yEdge = (y < h - 1 - y) ? y : (h - 1 - y);
                for (int x = 0; x < w; x++) {
                    int xEdge = (x < w - 1 - x) ? x : (w - 1 - x);
                    int band = (xEdge < yEdge) ? xEdge : yEdge;
                    Color32 c = clear;
                    if (band < 2) {
                        c = white;
                    } else if (band < 4) {
                        c = gray;
                    } else if (band < 6) {
                        c = black;
                    }
                    pixels[y * w + x] = c;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            tex.filterMode = (FilterMode)0;
            ((Object)tex).hideFlags = (HideFlags)61;
            _frame = tex;
            _frameW = w;
            _frameH = h;
        }
    }
}