using System;
using System.Collections.Generic;
using System.Text;
using PoolsCoop.Net;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PoolsCoop
{
    /// <summary>
    /// The co-op panel, built at run time from the game's own UI parts (Canvas, Button, TextMeshPro): the game's IL2CPP build has the
    /// IMGUI methods stripped out, so OnGUI is not an option. It hosts a game, lists friends who are hosting with one Join button each,
    /// and leaves. There is no code to type: friends find each other through Steam rich presence (see CoopLobby).
    ///
    /// Flat screen: F7 (or F8) opens and closes it, and a small "CO-OP" button sits in the corner of the screen whenever the mouse cursor
    /// is free (main menu, pause menu), so the panel can be found without knowing the key.
    ///
    /// VR: a "CO-OP" button stands under the game's own side buttons of the VR main menu (Settings / Exit VR / Quit Game); pointing at it
    /// and pulling the trigger opens the panel in front of you. In a chapter the panel appears next to the game's VR pause screen while the
    /// game is paused (menu button on the controller). Both are copies of the game's own world-space canvases (same raycasters and layer),
    /// so the controller rays press them exactly like the game's buttons. If those canvases are missing, the panel simply opens in the
    /// menu scenes in front of the player.
    /// </summary>
    internal static class CoopPanel
    {
        public static bool Visible;

        // flat
        private static GameObject _flatRoot;
        private static RectTransform _flatPanel;
        private static TMP_Text _hud;
        private static GameObject _cornerButton;

        // vr (rebuilt in every scene: they are copies of that scene's canvases)
        private static GameObject _vrRoot;
        private static RectTransform _vrPanel;
        private static GameObject _vrSideButton;
        private static string _vrScene = "";
        private static bool _vrTemplateMissing;
        private static bool _vrPausedShown;
        private static float _nextVrTry;
        private static float _vrBuiltAt;
        private static bool _testClicked;
        private static bool _cornerLogged;

        private static RectTransform _panel; // the panel the rows go into (flat or VR)
        private static TMP_FontAsset _font;
        private static readonly List<GameObject> Rows = new List<GameObject>();
        private static string _signature = "";

        public static int Opens;
        public static string LastOpenedBy = "";

        private static bool VrMode => Plugin.IsVrActive || Plugin.ForceVrPanel;

        public static void Toggle(string by = "code")
        {
            SetVisible(!Visible, by);
        }

        public static void SetVisible(bool on, string by)
        {
            if (on == Visible) return;
            Visible = on;
            if (Visible)
            {
                Opens++;
                LastOpenedBy = by;
                Plugin.EnsureSteam();
                Plugin.Logger.LogInfo($"co-op panel opened ({by})");
            }
            EnsureUi();
            if (VrMode)
            {
                if (_vrPanel != null) _vrPanel.gameObject.SetActive(Visible);
                if (Visible) PlaceVrPanel();
                _panel = _vrPanel;
            }
            else
            {
                if (_flatPanel != null) _flatPanel.gameObject.SetActive(Visible);
                _panel = _flatPanel;
            }
            _signature = ""; // force a rebuild
        }

        // ---- building --------------------------------------------------------------------------------

        private static void EnsureUi()
        {
            if (VrMode) EnsureVr();
            else EnsureFlat();
        }

        private static void EnsureFlat()
        {
            if (_flatRoot != null) return;
            _flatRoot = new GameObject("PoolsCoopUI");
            UnityEngine.Object.DontDestroyOnLoad(_flatRoot);
            var canvas = _flatRoot.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = _flatRoot.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _flatRoot.AddComponent<GraphicRaycaster>();

            _flatPanel = MakePanel(_flatRoot.transform, 620f, 0);
            _flatPanel.anchorMin = new Vector2(0f, 1f);
            _flatPanel.anchorMax = new Vector2(0f, 1f);
            _flatPanel.pivot = new Vector2(0f, 1f);
            _flatPanel.anchoredPosition = new Vector2(30f, -30f);
            _flatPanel.gameObject.SetActive(Visible);
            _panel = _flatPanel;

            var hudGo = new GameObject("Hud");
            hudGo.transform.SetParent(_flatRoot.transform, false);
            _hud = AddText(hudGo, "", 28, new Color(0.8f, 1f, 0.9f, 1f));
            var hrt = hudGo.GetComponent<RectTransform>();
            hrt.anchorMin = new Vector2(0.5f, 1f);
            hrt.anchorMax = new Vector2(0.5f, 1f);
            hrt.pivot = new Vector2(0.5f, 1f);
            hrt.anchoredPosition = new Vector2(0f, -14f);
            hrt.sizeDelta = new Vector2(1200f, 44f);
            _hud.alignment = TextAlignmentOptions.Center;

            // the corner button: visible whenever the cursor is free and the panel is closed
            _cornerButton = MakeButton(_flatRoot.transform, "CO-OP  (F7)", 26f, () => Toggle("corner button"), 0);
            var crt = _cornerButton.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(1f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(1f, 1f);
            crt.anchoredPosition = new Vector2(-24f, -24f);
            crt.sizeDelta = new Vector2(230f, 58f);
            UnityEngine.Object.Destroy(_cornerButton.GetComponent<LayoutElement>());
            _cornerButton.SetActive(false);
            Plugin.Logger.LogInfo("co-op panel: flat UI built (F7/F8 or the CO-OP button in the corner)");
        }

        private static RectTransform MakePanel(Transform parent, float width, int layer)
        {
            var panelGo = new GameObject("Panel");
            panelGo.layer = layer;
            panelGo.transform.SetParent(parent, false);
            var bg = panelGo.AddComponent<Image>();
            bg.color = new Color(0.04f, 0.05f, 0.07f, 0.9f);
            var rt = panelGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(width, 100f);
            var layout = panelGo.AddComponent<VerticalLayoutGroup>();
            var pad = new RectOffset();
            pad.left = 22; pad.right = 22; pad.top = 20; pad.bottom = 20;
            layout.padding = pad;
            layout.spacing = 12f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fit = panelGo.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rt;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static Canvas FindCanvas(Func<string, bool> pathTest)
        {
            foreach (var c in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c == null || c.renderMode != RenderMode.WorldSpace) continue;
                if (pathTest(PathOf(c.transform))) return c;
            }
            return null;
        }

        private static readonly HashSet<string> KeepOnCanvas = new HashSet<string>
        {
            "Transform", "RectTransform", "Canvas", "CanvasScaler", "GraphicRaycaster", "TrackedDeviceGraphicRaycaster", "CanvasGroup", "SortingGroup",
        };

        /// <summary>A copy of one of the game's world-space canvases with nothing in it but the canvas and its raycasters.</summary>
        private static GameObject CloneCanvas(Canvas template, string name)
        {
            var go = UnityEngine.Object.Instantiate(template.gameObject);
            go.name = name;
            go.transform.SetParent(null, true);
            for (int i = go.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                string n;
                try { n = c.GetIl2CppType().Name; } catch (Exception) { continue; }
                if (!KeepOnCanvas.Contains(n)) UnityEngine.Object.Destroy(c);
            }
            if (go.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>() == null)
            {
                try { go.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>(); }
                catch (Exception e) { Plugin.Logger.LogWarning($"VR panel: no XR UI raycaster ({e.Message})"); }
            }
            if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();
            var cv = go.GetComponent<Canvas>();
            cv.renderMode = RenderMode.WorldSpace;
            cv.sortingOrder = 500;
            var cam = FindHeadCamera();
            if (cam != null) cv.worldCamera = cam;
            go.SetActive(true);
            return go;
        }

        private static void EnsureVr()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != _vrScene)
            {
                // the old copies belonged to the previous scene and are gone with it
                _vrScene = scene;
                _vrRoot = null;
                _vrPanel = null;
                _vrSideButton = null;
                _vrTemplateMissing = false;
                _vrPausedShown = false;
                _nextVrTry = 0f;
            }
            if (_vrRoot != null || Time.realtimeSinceStartup < _nextVrTry) return;
            _nextVrTry = Time.realtimeSinceStartup + 2f;
            try
            {
                bool menu = !BodySync.IsGameplayScene(scene);
                Canvas side = menu ? FindCanvas(p => p.Contains("Side Buttons Panel") && p.EndsWith("/Canvas")) : null;
                Canvas pause = FindCanvas(p => p.EndsWith("PauseCanvasVR"));
                Canvas template = side ?? pause ?? FindCanvas(p => true);
                if (template == null) { _vrTemplateMissing = true; return; }

                _vrRoot = CloneCanvas(template, "PoolsCoopVRPanel");
                int layer = _vrRoot.layer;
                var rt = _vrRoot.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(820f, 900f);
                _vrRoot.transform.localScale = new Vector3(0.0012f, 0.0012f, 0.0012f);
                _vrPanel = MakePanel(_vrRoot.transform, 780f, layer);
                _vrPanel.anchorMin = _vrPanel.anchorMax = _vrPanel.pivot = new Vector2(0.5f, 0.5f);
                _vrPanel.anchoredPosition = Vector2.zero;
                _vrPanel.gameObject.SetActive(Visible);
                _panel = _vrPanel;
                _signature = "";

                if (side != null) BuildSideButton(side, layer);
                _vrBuiltAt = Time.realtimeSinceStartup;
                Plugin.Logger.LogInfo($"VR panel: built from '{PathOf(template.transform)}' (layer {layer}); side button {(_vrSideButton != null ? "added" : "not added")}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"VR panel failed: {e}");
                _vrTemplateMissing = true;
            }
        }

        /// <summary>A "CO-OP" button one step under the lowest of the game's VR side buttons, facing the same way.</summary>
        private static void BuildSideButton(Canvas anyside, int layer)
        {
            var sides = new List<Canvas>();
            foreach (var c in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (c != null && c.renderMode == RenderMode.WorldSpace && PathOf(c.transform).Contains("Side Buttons Panel") && c.transform.parent != null)
                    sides.Add(c);
            if (sides.Count == 0) return;
            sides.Sort((a, b) => a.transform.position.y.CompareTo(b.transform.position.y));
            Canvas low = sides[0];
            Vector3 step = sides.Count >= 2 ? low.transform.position - sides[1].transform.position : Vector3.down * 0.33f;

            _vrSideButton = CloneCanvas(low, "PoolsCoopVRSideButton");
            _vrSideButton.transform.SetPositionAndRotation(low.transform.position + step, low.transform.rotation);
            _vrSideButton.transform.localScale = low.transform.lossyScale;
            var srt = _vrSideButton.GetComponent<RectTransform>();
            srt.sizeDelta = low.GetComponent<RectTransform>().sizeDelta;
            var b = MakeButton(_vrSideButton.transform, "CO-OP", 0f, () => Toggle("VR side button"), layer);
            UnityEngine.Object.Destroy(b.GetComponent<LayoutElement>());
            var brt = b.GetComponent<RectTransform>();
            brt.anchorMin = Vector2.zero;
            brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero;
            brt.offsetMax = Vector2.zero;
            var label = b.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) { label.enableAutoSizing = true; label.fontSizeMin = 1f; label.fontSizeMax = 400f; }
        }

        private static Camera FindHeadCamera()
        {
            try
            {
                if (Backrooms.Features.VR.PoolsXROrigin.IsInstanced)
                {
                    var o = Backrooms.Features.VR.PoolsXROrigin.Instance;
                    if (o.xrOrigin != null && o.xrOrigin.Camera != null) return o.xrOrigin.Camera;
                }
            }
            catch (Exception) { }
            return CoopBehaviour.FlatCamera();
        }

        /// <summary>Puts the VR panel 1.2 m ahead of the head, turned towards the player; when paused, a little to the right of the pause screen.</summary>
        public static void PlaceVrPanel()
        {
            try
            {
                var cam = FindHeadCamera();
                if (cam == null || _vrRoot == null) return;
                Transform c = cam.transform;
                Vector3 fwd = c.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, fwd);
                Vector3 pos = c.position + fwd * 1.2f + Vector3.down * 0.1f;
                if (_vrPausedShown) pos = c.position + fwd * 1.15f + right * 0.85f + Vector3.down * 0.1f;
                _vrRoot.transform.position = pos;
                _vrRoot.transform.rotation = Quaternion.LookRotation(pos - c.position, Vector3.up);
            }
            catch (Exception) { }
        }

        public static void PlaceInFrontOfPlayer() => PlaceVrPanel();

        internal static TMP_FontAsset FontAsset() => Font();

        private static TMP_FontAsset Font()
        {
            if (_font != null) return _font;
            try
            {
                foreach (var t in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (t != null && t.font != null) { _font = t.font; break; }
                if (_font == null) _font = TMP_Settings.defaultFontAsset;
            }
            catch (Exception) { }
            return _font;
        }

        private static TMP_Text AddText(GameObject go, string text, float size, Color color)
        {
            var t = go.AddComponent<TextMeshProUGUI>();
            var f = Font();
            if (f != null) t.font = f;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.enableWordWrapping = true;
            t.raycastTarget = false;
            return t;
        }

        private static GameObject MakeButton(Transform parent, string label, float fontSize, Action onClick, int layer)
        {
            var go = new GameObject("Button");
            go.layer = layer;
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.16f, 0.36f, 0.5f, 1f);
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.3f, 0.6f, 0.8f, 1f);
            colors.selectedColor = new Color(0.22f, 0.45f, 0.62f, 1f);
            colors.pressedColor = new Color(0.1f, 0.25f, 0.4f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { try { onClick(); } catch (Exception e) { Plugin.Logger.LogWarning($"panel click failed: {e.Message}"); } }));
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = VrMode ? 78f : 56f;
            var textGo = new GameObject("Text");
            textGo.layer = layer;
            textGo.transform.SetParent(go.transform, false);
            var t = AddText(textGo, label, fontSize > 0f ? fontSize : 28f, Color.white);
            t.alignment = TextAlignmentOptions.Center;
            var trt = textGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            return go;
        }

        private static void Clear()
        {
            foreach (var r in Rows) if (r != null) UnityEngine.Object.Destroy(r);
            Rows.Clear();
        }

        private static float Scale(float size) => VrMode ? size * 1.3f : size;

        private static void AddLabel(string text, float size = 26f)
        {
            if (_panel == null) return;
            var go = new GameObject("Label");
            go.layer = _panel.gameObject.layer;
            go.transform.SetParent(_panel.transform, false);
            AddText(go, text, Scale(size), Color.white);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = Scale(size) + 8f;
            Rows.Add(go);
        }

        private static void AddButton(string label, Action onClick)
        {
            if (_panel == null) return;
            Rows.Add(MakeButton(_panel.transform, label, Scale(28f), onClick, _panel.gameObject.layer));
        }

        private static string Status(CoopSession s)
        {
            var sb = new StringBuilder();
            sb.Append(s.Role == SessionRole.Host ? "Hosting" : (s.Connected ? "Joined" : "Joining"));
            sb.Append(": ").Append(s.LocalName);
            foreach (var rp in s.Remotes.Values) sb.Append(", ").Append(rp.Name);
            return sb.ToString();
        }

        // ---- per frame -------------------------------------------------------------------------------

        private static bool _legacyInputBroken;

        /// <summary>Keys that open the panel: F7 and F8 (new Input System), with the old Input Manager as a fallback.</summary>
        public static void HandleKeys()
        {
            string key = null;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                if (kb.f7Key.wasPressedThisFrame) key = "F7";
                else if (kb.f8Key.wasPressedThisFrame) key = "F8";
            }
            if (key == null && !_legacyInputBroken)
            {
                try
                {
                    if (Input.GetKeyDown(KeyCode.F7)) key = "F7 (legacy input)";
                    else if (Input.GetKeyDown(KeyCode.F8)) key = "F8 (legacy input)";
                }
                catch (Exception) { _legacyInputBroken = true; }
            }
            if (key != null) Toggle(key);
        }

        private static bool CursorFree()
        {
            try { return Cursor.visible || Cursor.lockState != CursorLockMode.Locked; }
            catch (Exception) { return false; }
        }

        private static bool VrPaused()
        {
            try
            {
                var pause = FindCanvas(p => p.EndsWith("PauseCanvasVR"));
                if (pause == null) return false;
                var screen = pause.transform.Find("PauseScreen");
                return screen != null && screen.gameObject.activeInHierarchy;
            }
            catch (Exception) { return false; }
        }

        private static float _nextPauseCheck;
        private static bool _pausedCache;

        /// <summary>Called every frame from CoopBehaviour.Update. Rebuilds the rows only when what they show has changed.</summary>
        public static void Tick()
        {
            CoopSession s = Plugin.Session;
            string scene = SceneManager.GetActiveScene().name;
            bool inMenu = !BodySync.IsGameplayScene(scene) && scene != "loadMainMenu";

            if (VrMode)
            {
                if (scene == "loadMainMenu") return;
                EnsureVr();
                _panel = _vrPanel;
                if (_vrPanel != null && _vrPanel.gameObject.activeSelf != Visible) _vrPanel.gameObject.SetActive(Visible);

                if (!inMenu)
                {
                    // in a chapter: open next to the game's pause screen while paused, close when the game resumes
                    float now = Time.realtimeSinceStartup;
                    if (now >= _nextPauseCheck) { _nextPauseCheck = now + 0.25f; _pausedCache = VrPaused(); }
                    if (_pausedCache && !_vrPausedShown) { _vrPausedShown = true; SetVisible(true, "VR pause screen"); PlaceVrPanel(); }
                    else if (!_pausedCache && _vrPausedShown) { _vrPausedShown = false; SetVisible(false, "VR resume"); }
                }
                else if (Plugin.ForceVrPanel && _vrSideButton != null && !_testClicked && Time.realtimeSinceStartup - _vrBuiltAt > 4f)
                {
                    // test: press the side button the way a controller ray would (its onClick), then report where everything is
                    _testClicked = true;
                    _vrSideButton.GetComponentInChildren<Button>().onClick.Invoke();
                    var cam = FindHeadCamera();
                    Vector3 sb = _vrSideButton.transform.position, pp = _vrRoot.transform.position;
                    Plugin.Logger.LogInfo(FormattableString.Invariant(
                        $"VR panel test: after the side button click visible={Visible} openedBy='{LastOpenedBy}' sideButton=({sb.x:F2},{sb.y:F2},{sb.z:F2}) panel=({pp.x:F2},{pp.y:F2},{pp.z:F2}) head={(cam != null ? cam.transform.position.ToString("F2") : "none")} panelLayer={_vrPanel.gameObject.layer} raycaster={(_vrRoot.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>() != null)}"));
                }
                else if (_vrSideButton == null && (_vrTemplateMissing || _vrRoot != null) && !Visible && Opens == 0)
                {
                    // no side button could be made: just show the panel in the menu
                    SetVisible(true, "VR menu (no side button)");
                }
            }
            else
            {
                if (_flatRoot == null && !inMenu && !Visible && (s == null || !s.Connected)) return;
                EnsureFlat();
                _panel = _flatPanel;
                bool corner = !Visible && (inMenu || CursorFree()) && scene != "loadMainMenu";
                if (_cornerButton != null && _cornerButton.activeSelf != corner)
                {
                    _cornerButton.SetActive(corner);
                    if (corner && !_cornerLogged) { _cornerLogged = true; Plugin.Logger.LogInfo($"co-op panel: CO-OP corner button shown in scene '{scene}'"); }
                }
                if (_hud != null)
                {
                    string hud = (s != null && s.Connected && !Visible) ? Status(s) + "     (F7)" : "";
                    if (hud != "" && Plugin.PendingFollowScene != "") hud += "     host is in chapter " + Plugin.PendingFollowScene + ": press F7";
                    if (_hud.text != hud) _hud.text = hud;
                }
            }
            if (!Visible || _panel == null) return;

            bool steamReady = Plugin.SteamNative != null && ((ISteamSocial)Plugin.SteamNative).Ready;
            var games = Plugin.Lobby != null ? Plugin.Lobby.HostedGames : null;
            var sig = new StringBuilder();
            sig.Append(steamReady).Append('|').Append(s == null ? "none" : (s.Role + ":" + s.Connected + ":" + Status(s)));
            sig.Append('|').Append(Plugin.PendingFollowScene).Append('|').Append(scene).Append('|').Append(_panel.Pointer);
            if (games != null) foreach (var g in games) sig.Append('|').Append(g.HostId);
            string signature = sig.ToString();
            if (signature == _signature) return;
            _signature = signature;

            Clear();
            AddLabel(VrMode ? "POOLS Co-op" : "POOLS Co-op      (F7 closes)", 30f);
            if (!steamReady)
            {
                AddLabel("Waiting for Steam... Steam must be running and you signed in.");
            }
            else if (s == null)
            {
                AddLabel("You are " + Plugin.SteamNative.LocalName + ".");
                AddButton("Host a game", () => Plugin.StartHostSteam());
                if (games == null || games.Count == 0)
                {
                    AddLabel("No friend is hosting right now. A friend needs the game open with this mod and to press Host.", 22f);
                }
                else
                {
                    AddLabel("Friends hosting:");
                    for (int i = 0; i < games.Count && i < 6; i++)
                    {
                        ulong id = games[i].HostId;
                        AddButton("Join " + games[i].Name, () => Plugin.StartJoinSteam(id));
                    }
                }
            }
            else
            {
                AddLabel(Status(s));
                if (s.Role == SessionRole.Host && s.Remotes.Count == 0) AddLabel("Waiting for a friend. They see you in their co-op list and press Join.", 22f);
                if (s.Role == SessionRole.Client && !s.Connected) AddLabel("Connecting to the host... (the host must have the game open)", 22f);
                if (Plugin.PendingFollowScene != "")
                {
                    string sc = Plugin.PendingFollowScene;
                    AddLabel("The host started chapter " + sc + ". Starting it yourself may reset that chapter's autosave.", 22f);
                    AddButton("Go to the host's chapter " + sc, () => Plugin.FollowHost(sc));
                }
                else if (s.Connected)
                {
                    foreach (var rp in s.Remotes.Values)
                    {
                        if (!rp.HasPose) continue;
                        byte id = rp.Id;
                        AddButton("Go to " + rp.Name, () => Plugin.TeleportToPlayer(id));
                    }
                }
                AddButton(s.Role == SessionRole.Host ? "Stop hosting" : "Leave", () => Plugin.StopCoop());
            }
            AddButton("Close", () => SetVisible(false, "close button"));
        }
    }
}
