using System;
using System.Collections.Generic;
using System.Text;
using PoolsCoop.Net;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PoolsCoop
{
    /// <summary>
    /// The flat-screen co-op panel, built at run time from the game's own UI parts (Canvas, Button, TextMeshPro): the game's IL2CPP
    /// build has the IMGUI methods stripped out, so OnGUI is not an option. F7 opens and closes it; use it where the mouse cursor is
    /// free (the main menu, the pause menu). It hosts a game, lists friends who are hosting with one Join button each, and leaves.
    /// There is no code to type: friends find each other through Steam rich presence (see CoopLobby).
    /// The VR version of this panel is not built yet.
    /// </summary>
    internal static class CoopPanel
    {
        public static bool Visible;

        private static GameObject _root;
        private static RectTransform _panel;
        private static TMP_Text _hud;
        private static TMP_FontAsset _font;
        private static readonly List<GameObject> Rows = new List<GameObject>();
        private static string _signature = "";

        public static void Toggle()
        {
            Visible = !Visible;
            if (Visible) Plugin.EnsureSteam();
            EnsureRoot();
            if (_panel != null) _panel.gameObject.SetActive(Visible);
            _signature = ""; // force a rebuild
        }

        private static bool _vrBuilt;
        private static bool _vrFailedLogged;
        private static Canvas _canvas;

        private static void EnsureRoot()
        {
            if (_root != null)
            {
                // A headset can start after the panel was built (the game switches to VR late): rebuild as a world-space canvas then.
                if ((Plugin.IsVrActive || Plugin.ForceVrPanel) && !_vrBuilt) ConvertToWorldSpace();
                return;
            }
            _root = new GameObject("PoolsCoopUI");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            _canvas = canvas;
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            var panelGo = new GameObject("Panel");
            panelGo.transform.SetParent(_root.transform, false);
            var bg = panelGo.AddComponent<Image>();
            bg.color = new Color(0.04f, 0.05f, 0.07f, 0.88f);
            _panel = panelGo.GetComponent<RectTransform>();
            _panel.anchorMin = new Vector2(0f, 1f);
            _panel.anchorMax = new Vector2(0f, 1f);
            _panel.pivot = new Vector2(0f, 1f);
            _panel.anchoredPosition = new Vector2(30f, -30f);
            _panel.sizeDelta = new Vector2(620f, 100f);
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
            panelGo.SetActive(Visible);

            var hudGo = new GameObject("Hud");
            hudGo.transform.SetParent(_root.transform, false);
            _hud = AddText(hudGo, "", 28, new Color(0.8f, 1f, 0.9f, 1f));
            var hrt = hudGo.GetComponent<RectTransform>();
            hrt.anchorMin = new Vector2(0.5f, 1f);
            hrt.anchorMax = new Vector2(0.5f, 1f);
            hrt.pivot = new Vector2(0.5f, 1f);
            hrt.anchoredPosition = new Vector2(0f, -14f);
            hrt.sizeDelta = new Vector2(1200f, 44f);
            _hud.alignment = TextAlignmentOptions.Center;
            if (Plugin.IsVrActive || Plugin.ForceVrPanel) ConvertToWorldSpace();
        }

        /// <summary>
        /// VR: an overlay canvas is not drawn in a headset, so the same panel becomes a world-space canvas about 1.2 m in front of the player,
        /// with the XR Interaction Toolkit's UI raycaster so the controllers' rays can press the buttons. NOT TESTED with a headset
        /// (the owner cannot run VR yet); every step is guarded so a failure here can never break the rest of the mod.
        /// </summary>
        private static void ConvertToWorldSpace()
        {
            try
            {
                if (_root == null || _canvas == null) return;
                _vrBuilt = true;
                _canvas.renderMode = RenderMode.WorldSpace;
                var cam = FindHeadCamera();
                if (cam != null) _canvas.worldCamera = cam;
                var scaler = _root.GetComponent<CanvasScaler>();
                if (scaler != null) UnityEngine.Object.Destroy(scaler); // world-space canvases are sized by their scale, not a reference resolution
                _root.transform.localScale = new Vector3(0.0011f, 0.0011f, 0.0011f);
                var rt = _root.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(760f, 700f);
                try { _root.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>(); }
                catch (Exception e) { Plugin.Logger.LogWarning($"VR panel: no XR UI raycaster ({e.Message}); mouse/gamepad clicks only"); }
                // anchor the panel inside the canvas rect so it is not pushed to the corner of a huge rect
                if (_panel != null)
                {
                    _panel.anchorMin = new Vector2(0.5f, 0.5f);
                    _panel.anchorMax = new Vector2(0.5f, 0.5f);
                    _panel.pivot = new Vector2(0.5f, 0.5f);
                    _panel.anchoredPosition = Vector2.zero;
                }
                if (_hud != null) _hud.gameObject.SetActive(false);
                PlaceInFrontOfPlayer();
                Plugin.Logger.LogInfo("VR panel: converted to a world-space canvas");
            }
            catch (Exception e)
            {
                if (!_vrFailedLogged) { _vrFailedLogged = true; Plugin.Logger.LogWarning($"VR panel failed: {e}"); }
            }
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

        /// <summary>Puts the panel 1.2 m ahead of the player's head, turned towards them, level with the eyes.</summary>
        public static void PlaceInFrontOfPlayer()
        {
            try
            {
                var cam = FindHeadCamera();
                if (cam == null || _root == null) return;
                Transform c = cam.transform;
                Vector3 fwd = c.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                fwd.Normalize();
                Vector3 pos = c.position + fwd * 1.2f;
                _root.transform.position = pos;
                _root.transform.rotation = Quaternion.LookRotation(pos - c.position, Vector3.up);
            }
            catch (Exception) { }
        }

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

        private static void Clear()
        {
            foreach (var r in Rows) if (r != null) UnityEngine.Object.Destroy(r);
            Rows.Clear();
        }

        private static void AddLabel(string text, float size = 26f)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(_panel.transform, false);
            AddText(go, text, size, Color.white);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = size + 8f;
            Rows.Add(go);
        }

        private static void AddButton(string label, Action onClick)
        {
            var go = new GameObject("Button");
            go.transform.SetParent(_panel.transform, false);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.16f, 0.36f, 0.5f, 1f);
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(0.3f, 0.6f, 0.8f, 1f);
            colors.pressedColor = new Color(0.1f, 0.25f, 0.4f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => { try { onClick(); } catch (Exception e) { Plugin.Logger.LogWarning($"panel click failed: {e.Message}"); } }));
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = 56f;
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var t = AddText(textGo, label, 28, Color.white);
            t.alignment = TextAlignmentOptions.Center;
            var trt = textGo.GetComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            Rows.Add(go);
        }

        private static string Status(CoopSession s)
        {
            var sb = new StringBuilder();
            sb.Append(s.Role == SessionRole.Host ? "Hosting" : (s.Connected ? "Joined" : "Joining"));
            sb.Append(": ").Append(s.LocalName);
            foreach (var rp in s.Remotes.Values) sb.Append(", ").Append(rp.Name);
            return sb.ToString();
        }

        /// <summary>Called every frame from CoopBehaviour.Update. Rebuilds the rows only when what they show has changed.</summary>
        public static void Tick()
        {
            CoopSession s = Plugin.Session;

            // VR: nobody can press F7 in a headset, so the panel is simply open in the menu scenes and closed in the chapters.
            if (Plugin.IsVrActive)
            {
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                bool inMenu = !BodySync.IsGameplayScene(scene) && scene != "loadMainMenu";
                if (inMenu != Visible)
                {
                    Visible = inMenu;
                    if (Visible) Plugin.EnsureSteam();
                    EnsureRoot();
                    if (_panel != null) _panel.gameObject.SetActive(Visible);
                    _signature = "";
                    if (Visible) PlaceInFrontOfPlayer();
                }
            }

            if (_root == null && !Visible && (s == null || !s.Connected)) return;
            EnsureRoot();

            if (_hud != null)
            {
                string hud = (s != null && s.Connected && !Visible) ? Status(s) + "     (F7)" : "";
                if (hud != "" && Plugin.PendingFollowScene != "") hud += "     host is in chapter " + Plugin.PendingFollowScene + ": press F7";
                if (_hud.text != hud) _hud.text = hud;
            }
            if (!Visible) return;

            bool steamReady = Plugin.SteamNative != null && ((ISteamSocial)Plugin.SteamNative).Ready;
            var games = Plugin.Lobby != null ? Plugin.Lobby.HostedGames : null;
            var sig = new StringBuilder();
            sig.Append(steamReady).Append('|').Append(s == null ? "none" : (s.Role + ":" + s.Connected + ":" + Status(s)));
            sig.Append('|').Append(Plugin.PendingFollowScene).Append('|').Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            if (games != null) foreach (var g in games) sig.Append('|').Append(g.HostId);
            string signature = sig.ToString();
            if (signature == _signature) return;
            _signature = signature;

            Clear();
            AddLabel("POOLS Co-op      (F7 closes)", 30f);
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
                    string scene = Plugin.PendingFollowScene;
                    AddLabel("The host started chapter " + scene + ". Starting it yourself may reset that chapter's autosave.", 22f);
                    AddButton("Go to the host's chapter " + scene, () => Plugin.FollowHost(scene));
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
        }
    }
}
