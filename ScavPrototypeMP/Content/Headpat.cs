using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using UnityEngine;

namespace Together {
    // local pat minigame. arm2 idle. while held, arm3 is the bottom sixth and arm1 is the next two sixths up.
    // progress needs the hand on the head inside one of those two slots. the circle sits on the head.
    public static class HeadpatSession {
        const float StartDistance = 5f;
        const float KeepDistance = 6.5f;
        const float HoldSeconds = 3f;
        const double BuffCooldownSeconds = 300.0;
        // threaten is the smallest stock trauma gain (+5). half of that.
        const float TraumaRelief = 2.5f;
        const float MoodGain = 10f;
        const float MoveSpeed = 40f;
        const float HandLimit = 524f;
        const float HandRate = 60f;

        static readonly Dictionary<ushort, double> BuffReadyAt = new Dictionary<ushort, double>();

        static bool _open;
        static NetBody _self;
        static NetBody _target;
        static float _held;
        static Vector2 _lastMouse;
        static bool _haveMouse;
        static int _species;
        static Texture2D _arms;
        static Color32[] _armPix;
        static readonly Texture2D[] _heads = new Texture2D[3];
        static readonly Color32[][] _headPix = new Color32[3][];
        static readonly Vector2[] ArmHot = {
            new Vector2(146f, 149f),
            new Vector2(127f, 139f),
            new Vector2(139f, 150f),
        };
        static int _armW;
        static int _armH;
        static Texture2D _vignette;
        static Vector2 _handPos;
        static Vector2 _handVel;
        static float _handSteps;
        static float _patWait;
        static Vector2 _headKick;

        public static bool Open => _open;

        public static bool Blocks(Body body) {
            return _open && body != null && PlayerCamera.main != null && PlayerCamera.main.body == body;
        }

        [CTPostInit]
        static void Init() {
            ServerMain.ServerCustomCommandsDict["pat_done"] = (plr, cmd, args) => {
                if (plr == null || args == null || args.Length < 2 || !ushort.TryParse(args[1], out ushort targetId)) {
                    return;
                }
                if (!NetBody.TryGetNetBodyFromId(targetId, out NetBody victim) || victim.plr == null) {
                    return;
                }
                Finish(plr, victim);
            };
            ClientMain.ClientCustomCommandsDict["pat_fx"] = (cmd, reader) => {
                reader.Get(out ushort id);
                if (!NetBody.TryGetNetBodyFromId(id, out NetBody who)) {
                    return;
                }
                PlayFx(who);
            };
        }

        public static void Request(NetBody self, NetBody other) {
            if (_open || self == null || other == null || self == other || self.plr == null || other.plr == null) {
                return;
            }
            if (!Pattable(self.body) || !Pattable(other.body) || Tied(self, other)) {
                return;
            }
            if (!Near(self.body, other.body, StartDistance)) {
                if (PlayerCamera.main != null) {
                    PlayerCamera.main.DoAlert(Lang.Get("pat_far"));
                }
                return;
            }
            if (MinigameBase.main != null && MinigameBase.main.currentMinigame != null) {
                return;
            }
            _self = self;
            _target = other;
            _held = 0f;
            _haveMouse = false;
            _handPos = Vector2.zero;
            _handVel = Vector2.zero;
            _handSteps = 0f;
            _patWait = 0f;
            _headKick = Vector2.zero;
            _species = other.body.charType;
            if (_species < 0 || _species > 2) {
                _species = 0;
            }
            _open = true;
            EnsureArt();
        }

        public static void Tick() {
            if (!_open) {
                return;
            }
            if (_self == null || _target == null || _self.body == null || _target.body == null || !Pattable(_target.body) || !Near(_self.body, _target.body, KeepDistance)) {
                Close();
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) {
                Close();
                return;
            }
            Vector2 mouse = GuiMouse();
            float dt = Time.unscaledDeltaTime;
            StepHand(Time.deltaTime);
            _headKick = Vector2.MoveTowards(_headKick, Vector2.zero, 70f * UiScale() * dt);
            bool moving = false;
            if (_haveMouse && dt > 0.0001f) {
                moving = (mouse - _lastMouse).magnitude / dt >= MoveSpeed;
            }
            _lastMouse = mouse;
            _haveMouse = true;
            Rect zone = Zone();
            int frame = ArmFrame(zone);
            bool slot = frame != 1;
            bool touching = false;
            if (Input.GetMouseButton(0)) {
                Rect headRect = HeadRect(zone);
                Rect armRect = ArmRect(zone, frame);
                touching = SpritesTouch(headRect, armRect, frame);
                if (touching && slot) {
                    _patWait -= dt;
                    if (_patWait <= 0f) {
                        PlayPatSound();
                        _headKick = UnityEngine.Random.insideUnitCircle * (16f * UiScale());
                        _patWait = UnityEngine.Random.Range(1f, 1.5f);
                    }
                }
                if (touching && slot && moving) {
                    _held += dt;
                    if (_held >= HoldSeconds) {
                        Complete();
                    }
                }
            } else {
                _held = 0f;
                _patWait = 0f;
            }
        }

        public static void Draw() {
            if (!_open) {
                return;
            }
            EnsureArt();
            EnsureVignette();
            Rect zone = Zone();
            int frame = ArmFrame(zone);
            Rect headRect = HeadRect(zone);
            Color prev = GUI.color;
            GUI.color = Color.white;
            if (_vignette != null) {
                Vector2 origin = headRect.center;
                float reachX = Mathf.Max(origin.x, Screen.width - origin.x);
                float reachY = Mathf.Max(origin.y, Screen.height - origin.y);
                Rect fade = new Rect(origin.x - reachX, origin.y - reachY, reachX * 2f, reachY * 2f);
                GUI.DrawTexture(fade, _vignette, ScaleMode.StretchToFill, true);
            }
            Texture2D head = _heads[Mathf.Clamp(_species, 0, 2)];
            if (head != null) {
                GUI.DrawTexture(headRect, head, ScaleMode.StretchToFill, true);
            }
            if (_arms != null && _armW > 0) {
                float slice = _armW / (float)_arms.width;
                Rect uv = new Rect(frame * slice, 0f, slice, 1f);
                Rect arm = ArmRect(zone, frame);
                GUI.DrawTextureWithTexCoords(arm, _arms, uv, true);
            }
            GUI.color = prev;
        }

        static void Complete() {
            NetBody self = _self;
            NetBody target = _target;
            Close();
            if (self == null || target == null || self.plr == null || target.plr == null) {
                return;
            }
            if (Net.IsServer) {
                Finish(self.plr, target);
                return;
            }
            NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put("pat_done " + target.netId.id.ToString());
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        static void Finish(ScavPlayer from, NetBody victim) {
            if (!Net.IsServer || from == null || victim == null || from.body == null || victim.body == null || victim.plr == null) {
                return;
            }
            Body body = victim.body;
            if (!Pattable(from.body) || !Pattable(body) || !Near(from.body, body, KeepDistance)) {
                from.Server_DoAlertSingle("<key:pat_far>");
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            // the body under the hand. the patter's player record can point at the wrong body.
            if (body != from.body && BuffReady(victim.plr.playerId.id, now)) {
                body.traumaAmount = Mathf.Max(0f, body.traumaAmount - TraumaRelief);
                body.happiness = Mathf.Clamp(body.happiness + MoodGain, -100f, 100f);
                victim.last_mood = body.happiness;
                BuffReadyAt[victim.plr.playerId.id] = now;
                MedicalSync.Server_QueueSendCharacterHealth(victim, true);
            }
            PlayFx(victim);
            NetDataWriter writer = Multiplayer.CreateNamedWriter("CustomCommand");
            writer.Put("pat_fx");
            writer.Put(victim.netId.id);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
        }

        static bool BuffReady(ushort id, double now) {
            if (!BuffReadyAt.TryGetValue(id, out double at)) {
                return true;
            }
            if (now - at >= BuffCooldownSeconds) {
                BuffReadyAt.Remove(id);
                return true;
            }
            return false;
        }

        static void PlayFx(NetBody who) {
            if (who == null || who.body == null) {
                return;
            }
            PantSound pant = who.body.GetComponent<PantSound>();
            if (pant != null) {
                pant.Bark();
            }
            if (who.body.talker == null) {
                return;
            }
            List<string> lines = Locale.GetCharacter("traderhugsuccess", who.body.charType);
            if (lines != null && lines.Count > 0) {
                Talker_Talk_MultiplayerPatch.Force(who.body.talker, lines, null, true, true);
            }
        }

        static void Close() {
            _open = false;
            _self = null;
            _target = null;
            _held = 0f;
            _haveMouse = false;
            _handVel = Vector2.zero;
            _headKick = Vector2.zero;
            _patWait = 0f;
        }

        static void PlayPatSound() {
            Body body = _target != null ? _target.body : null;
            if (body == null) {
                return;
            }
            Sound.Play(
                "backpack" + UnityEngine.Random.Range(1, 3),
                body.transform.position,
                false,
                false,
                body.transform,
                1f,
                UnityEngine.Random.Range(0.8f, 1.2f));
        }

        static int ArmFrame(Rect zone) {
            if (!Input.GetMouseButton(0) || zone.height <= 1f || Screen.height <= 1f) {
                return 1;
            }
            float t = HandGui().y / Screen.height;
            if (t >= (5f / 6f)) {
                return 2;
            }
            if (t >= 0.5f) {
                return 0;
            }
            return 1;
        }

        static Rect HeadRect(Rect zone) {
            Rect headRect = new Rect(0f, 0f, zone.width * 3f, zone.height * 3f);
            headRect.center = new Vector2(zone.center.x, zone.yMax);
            headRect.position += _headKick;
            return headRect;
        }

        static Rect ArmRect(Rect zone, int frame) {
            Vector2 hot = ArmHot[frame];
            float armW = zone.width * 2.625f;
            float armH = zone.height * 2.625f;
            float sx = _armW > 0 ? armW / _armW : 1f;
            float sy = _armH > 0 ? armH / _armH : 1f;
            Vector2 paw = HandGui();
            return new Rect(paw.x - hot.x * sx, paw.y - hot.y * sy, armW, armH);
        }

        static bool SpritesTouch(Rect headRect, Rect armRect, int frame) {
            int species = Mathf.Clamp(_species, 0, 2);
            Color32[] headPix = _headPix[species];
            Texture2D head = _heads[species];
            if (headPix == null || head == null || _armPix == null || _armW <= 0 || _armH <= 0) {
                return false;
            }
            float x0 = Mathf.Max(headRect.xMin, armRect.xMin);
            float y0 = Mathf.Max(headRect.yMin, armRect.yMin);
            float x1 = Mathf.Min(headRect.xMax, armRect.xMax);
            float y1 = Mathf.Min(headRect.yMax, armRect.yMax);
            if (x1 - x0 < 1f || y1 - y0 < 1f) {
                return false;
            }
            if (headRect.width < 1f || headRect.height < 1f || armRect.width < 1f || armRect.height < 1f) {
                return false;
            }
            int headW = head.width;
            int headH = head.height;
            int sheetW = _armW * 3;
            const float step = 6f;
            for (float y = y0; y < y1; y += step) {
                int hy = headH - 1 - Mathf.Clamp((int)(((y - headRect.y) / headRect.height) * headH), 0, headH - 1);
                int ay = _armH - 1 - Mathf.Clamp((int)(((y - armRect.y) / armRect.height) * _armH), 0, _armH - 1);
                for (float x = x0; x < x1; x += step) {
                    int hx = Mathf.Clamp((int)(((x - headRect.x) / headRect.width) * headW), 0, headW - 1);
                    int ax = frame * _armW + Mathf.Clamp((int)(((x - armRect.x) / armRect.width) * _armW), 0, _armW - 1);
                    if (headPix[hy * headW + hx].a > 40 && _armPix[ay * sheetW + ax].a > 40) {
                        return true;
                    }
                }
            }
            return false;
        }

        // same spring as MinigameBase.UpdateHandPhysics. strength speeds the catch, pain shakes the target.
        static void StepHand(float dt) {
            Body body = _self != null ? _self.body : null;
            if (body == null || dt <= 0f) {
                return;
            }
            float scale = UiScale();
            Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            float shake = body.averagePain * Mathf.Clamp01(1f - body.skills.RESFrom10 * 0.06f) * ShakeForce();
            float time = Time.time;
            Vector2 wobble = new Vector2(
                Mathf.PerlinNoise(time * 2.9f, time * 2.7f) - 0.5f,
                Mathf.PerlinNoise(time * 2.86f, time * 2.2f) - 0.5f) * shake;
            Vector2 mouse = ((Vector2)Input.mousePosition - center) / scale + wobble;
            mouse.x = Mathf.Clamp(mouse.x, -HandLimit, HandLimit);
            mouse.y = Mathf.Clamp(mouse.y, -HandLimit, HandLimit);
            float conscious = body.consciousness * 0.01f;
            float follow = 0.25f * conscious;
            float accel = (4f + body.skills.STRFrom10 * 0.3f) * conscious;
            const float drag = 1.5f;
            float step = 1f / HandRate;
            _handSteps += dt * HandRate;
            int guard = 0;
            while (_handSteps > 1f && guard < 8) {
                Vector2 desired = Vector2.ClampMagnitude((mouse - _handPos) * follow, 75f);
                _handVel = Vector2.Lerp(_handVel, desired, accel * step);
                _handVel = Vector2.Lerp(_handVel, Vector2.zero, step * drag);
                _handPos += _handVel * step * 120f;
                _handSteps -= 1f;
                guard++;
            }
        }

        static Vector2 HandGui() {
            float scale = UiScale();
            Vector2 screen = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) + _handPos * scale;
            return new Vector2(screen.x, Screen.height - screen.y);
        }

        static float UiScale() {
            if (PlayerCamera.main == null || PlayerCamera.main.mainCanvas == null) {
                return 1f;
            }
            float scale = PlayerCamera.main.mainCanvas.transform.localScale.y;
            if (scale < 0.01f) {
                return 1f;
            }
            return scale;
        }

        static float ShakeForce() {
            try {
                return WorldGeneration.GetRunSettingFloat("minigamehandshake");
            } catch {
                return 1f;
            }
        }

        static void EnsureVignette() {
            if (_vignette != null) {
                return;
            }
            const int n = 256;
            Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            Color[] pix = new Color[n * n];
            float half = (n - 1) * 0.5f;
            for (int y = 0; y < n; y++) {
                for (int x = 0; x < n; x++) {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float radius = Mathf.Sqrt(dx * dx + dy * dy);
                    // dark through the middle, screen edges stay see-through
                    float cover = 1f - Mathf.SmoothStep(0.05f, 1.15f, radius);
                    float alpha = Mathf.Clamp01(Mathf.Lerp(0.2f, 0.94f, Mathf.Clamp01(cover)) * 3f);
                    pix[y * n + x] = new Color(0f, 0f, 0f, alpha);
                }
            }
            tex.SetPixels(pix);
            tex.Apply(false, true);
            _vignette = tex;
        }

        static Rect Zone() {
            float width = Screen.width * 0.42f;
            float height = _armW > 0 ? width * (_armH / (float)_armW) : width * (280f / 688f);
            return new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        }

        static Vector2 GuiMouse() {
            Vector3 m = Input.mousePosition;
            return new Vector2(m.x, Screen.height - m.y);
        }

        static bool Pattable(Body body) {
            return body != null && body.alive && body.conscious;
        }

        static bool Tied(NetBody a, NetBody b) {
            return a.piggybacking_on == b || b.piggybacking_on == a || a.carrying_person == b || b.carrying_person == a;
        }

        static bool Near(Body a, Body b, float max) {
            if (a == null || b == null) {
                return false;
            }
            Vector2 delta = (Vector2)a.transform.position - (Vector2)b.transform.position;
            return delta.sqrMagnitude <= max * max;
        }

        static void EnsureArt() {
            if (_arms != null) {
                return;
            }
            Texture2D a1 = Load("headpat_arm1.png");
            Texture2D a2 = Load("headpat_arm2.png");
            Texture2D a3 = Load("headpat_arm3.png");
            _heads[0] = Load("headpat_head0.png");
            _heads[1] = Load("headpat_head1.png");
            _heads[2] = Load("headpat_head2.png");
            if (a1 == null || a2 == null || a3 == null) {
                return;
            }
            _armW = a1.width;
            _armH = a1.height;
            Texture2D sheet = new Texture2D(_armW * 3, _armH, TextureFormat.RGBA32, false);
            sheet.filterMode = FilterMode.Bilinear;
            sheet.wrapMode = TextureWrapMode.Clamp;
            sheet.SetPixels(0, 0, _armW, _armH, a1.GetPixels());
            sheet.SetPixels(_armW, 0, _armW, _armH, a2.GetPixels());
            sheet.SetPixels(_armW * 2, 0, _armW, _armH, a3.GetPixels());
            sheet.Apply(false, false);
            _arms = sheet;
            _armPix = sheet.GetPixels32();
            for (int i = 0; i < 3; i++) {
                if (_heads[i] != null) {
                    _headPix[i] = _heads[i].GetPixels32();
                }
            }
        }

        static Texture2D Load(string name) {
            byte[] bytes = CoopModAssets.ReadResource("textures." + name);
            if (bytes == null) {
                return null;
            }
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, bytes)) {
                return null;
            }
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        [HarmonyPatch(typeof(Body), nameof(Body.Attack))]
        static class BlockAttack {
            static bool Prefix(Body __instance) {
                if (!Blocks(__instance)) {
                    return true;
                }
                // the swing did not happen. drop this so the next real attack is not swallowed.
                Body_Attack_MultiplayerPatch.ForceOnlyRetrieveAttackInfo = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Body), "DoUnarmedAttack")]
        static class BlockUnarmed {
            static bool Prefix(Body __instance) {
                return !Blocks(__instance);
            }
        }
    }
}