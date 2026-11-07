using System;
using System.Collections.Generic;
using System.Net;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using PoolsCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoolsCoop
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "dev.poolscoop.probe";
        public const string PluginName = "PoolsCoop";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Logger;
        internal static CoopSession Session;
        internal static IDiagnosticSource TransportDiag;
        internal static SteamP2PNative SteamProbe; // only set by the Test.SteamSelfProbe switch
        internal static bool BotEnabled;
        internal static int AutoQuitSeconds;
        internal static int PoseHz = 60;
        internal static bool WaterProbe;       // test-only: drops a synthetic puppet through a water surface
        internal static bool BodyBot;          // test-only: stand-in for players pushing doors
        internal static long BodyBotBase;
        internal static BodySync Bodies;
        internal static EventSync Events;
        internal static bool EventBot;         // test-only: fire a few trigger events and check they are replayed
        internal static bool ForceVrPanel;     // test-only: build the VR (world-space) panel without a headset, to see that it builds and draws
        internal static bool AutoFollowHost;   // follow the host into the chapter without asking (tests); players get a Follow button instead
        internal static bool SaveGuard;        // test-only: block every progress/achievement write (see SaveGuard.cs)
        internal static bool ReconEnabled;
        internal static string ReconLoadScene = "";

        // Bot test counters (read by CoopBehaviour, written by the PoseReceived handler).
        internal static int PoseOk;
        internal static int PoseBad;

        /// <summary>True when a headset is really running. (PoolsVRManager.IsVRModeActive is True even with no headset, so it cannot be used.)</summary>
        internal static bool IsVrActive
        {
            get
            {
                try { return UnityEngine.XR.XRSettings.isDeviceActive; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Parses Steam IDs separated by commas, semicolons or spaces. Invalid entries are logged and skipped.</summary>
        internal static List<ulong> ParseIds(string text)
        {
            var ids = new List<ulong>();
            foreach (string part in (text ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (ulong.TryParse(part, out ulong id) && id > 76561197960265728UL) ids.Add(id); // 64-bit Steam IDs of normal accounts
                else Logger.LogWarning($"co-op: ignoring '{part}', not a 17-digit Steam ID");
            }
            return ids;
        }

        // ---- session lifetime (used by the config start and by the in-game panel) -----------------

        internal static readonly StopwatchClock SharedClock = new StopwatchClock();
        internal static SteamP2PNative SteamNative;
        internal static CoopLobby Lobby;
        internal static SteamTransport HostSteamTransport;
        internal static bool ShowPanelAtStart;
        internal static bool AutoHost;         // test-only: press "Host a game" by itself, then "Stop hosting" later
        internal static int ShotAtSeconds;

        // ---- following the host into a chapter -----------------------------------------------------

        /// <summary>The chapter the host is in while we are somewhere else; the panel offers a button for it.</summary>
        internal static string PendingFollowScene = "";
        internal static string FollowScene = "";
        internal static float FollowStartedAt;
        internal static bool FollowFallbackUsed;

        internal static void OnHostSceneChanged(string hostScene)
        {
            Logger.LogInfo($"co-op: host scene is now '{hostScene}' (this game is in '{SceneManager.GetActiveScene().name}')");
            if (!BodySync.IsGameplayScene(hostScene)) { PendingFollowScene = ""; return; }
            if (SceneManager.GetActiveScene().name == hostScene) { PendingFollowScene = ""; return; }
            if (AutoFollowHost) FollowHost(hostScene);
            else PendingFollowScene = hostScene;
        }

        /// <summary>
        /// Go where the host is. Uses the game's own chapter start (SceneLoader.Load with the Level asset of that scene) so loading screens
        /// and save handling behave as when the player presses Play; a direct scene load is only the fallback.
        /// Note this is the same as pressing Play on that chapter: the game may reset that chapter's autosave.
        /// </summary>
        internal static void FollowHost(string scene)
        {
            PendingFollowScene = "";
            FollowScene = scene;
            FollowStartedAt = UnityEngine.Time.realtimeSinceStartup;
            FollowFallbackUsed = false;
            try
            {
                foreach (var lv in Resources.FindObjectsOfTypeAll<Backrooms.Features.Levels.Level>())
                {
                    if (lv != null && lv.scene == scene)
                    {
                        Logger.LogInfo($"co-op: following the host into '{scene}' through the game's chapter start");
                        Backrooms.Scripts.SceneLoader.Load(lv, false);
                        return;
                    }
                }
            }
            catch (Exception e) { Logger.LogWarning($"co-op: chapter start failed ({e.Message}), loading the scene directly"); }
            Logger.LogInfo($"co-op: no Level asset found for '{scene}', loading the scene directly");
            FollowFallbackUsed = true;
            SceneManager.LoadScene(scene);
        }

        /// <summary>Called every frame: finishes a follow, or loads the scene directly if the loading screen got stuck.</summary>
        internal static void TickFollow(float now)
        {
            if (FollowScene == "") return;
            string active = SceneManager.GetActiveScene().name;
            if (active == FollowScene) { Logger.LogInfo($"co-op: arrived in '{FollowScene}'"); FollowScene = ""; return; }
            if (!FollowFallbackUsed && now - FollowStartedAt > 15f && active != FollowScene)
            {
                FollowFallbackUsed = true;
                Logger.LogWarning($"co-op: still in '{active}' 15 s after the follow started, loading '{FollowScene}' directly");
                SceneManager.LoadScene(FollowScene);
            }
            else if (now - FollowStartedAt > 60f)
            {
                Logger.LogWarning($"co-op: gave up following into '{FollowScene}'");
                FollowScene = "";
            }
        }

        /// <summary>Move the local player next to another player (best effort; used by the panel's "Go to" button).</summary>
        internal static bool TeleportToPlayer(byte id)
        {
            var s = Session;
            if (s == null || !s.Remotes.TryGetValue(id, out RemotePlayer rp) || !rp.HasPose) return false;
            var h = rp.Pose.Head.Pos;
            Vector3 head = new Vector3(h.X + 0.9f, h.Y, h.Z + 0.9f);
            try
            {
                if (IsVrActive && Backrooms.Features.VR.PoolsXROrigin.IsInstanced)
                {
                    var o = Backrooms.Features.VR.PoolsXROrigin.Instance;
                    o.xrOrigin.MoveCameraToWorldLocation(head);
                    if (o.rb != null) { o.rb.linearVelocity = Vector3.zero; }
                    return true;
                }
                if (FPSController.IsInstanced)
                {
                    Transform t = FPSController.Instance.transform;
                    Vector3 feet = new Vector3(head.x, head.y - 1.6f, head.z);
                    var rb = t.GetComponent<Rigidbody>();
                    t.position = feet;
                    if (rb != null) { rb.position = feet; rb.linearVelocity = Vector3.zero; }
                    return true;
                }
            }
            catch (Exception e) { Logger.LogWarning($"co-op: teleport failed: {e.Message}"); }
            return false;
        }

        /// <summary>Hooks a freshly created session up to the shared bodies and the log.</summary>
        internal static void AttachSession(CoopSession session)
        {
            Session = session;
            Bodies = new BodySync();
            Events = new EventSync();
            var sess = session;
            session.GameEventReceived += (id, kind, from) => Events?.OnRemote(id, kind, from, UnityEngine.Time.realtimeSinceStartup);
            session.BodyOwnerChanged += v => Bodies?.OnOwnerChanged(v, sess);
            session.BodyStateReceived += v => Bodies?.OnState(v, UnityEngine.Time.realtimeSinceStartup);
            session.PlayerJoined += p => Logger.LogInfo($"co-op: player joined id={p.Id} name='{p.Name}'");
            session.PlayerLeft += p => Logger.LogInfo($"co-op: player left id={p.Id} name='{p.Name}'");
            session.HostSceneChanged += s => OnHostSceneChanged(s);
            if (BotEnabled)
            {
                session.PoseReceived += (id, timeMs, pose) =>
                {
                    var expected = Core.BotPose.At(timeMs, id);
                    if (pose.MaxPosDistance(expected) < 1e-4f) PoseOk++; else PoseBad++;
                };
            }
            if (session.Role == SessionRole.Host)
                try { session.SetLocalScene(SceneManager.GetActiveScene().name); } catch (Exception) { }
        }

        /// <summary>Starts the Steam side (friends list, rich presence) the first time it is needed. True when Steam is ready now.</summary>
        internal static bool EnsureSteam()
        {
            if (SteamNative == null)
            {
                SteamNative = new SteamP2PNative();
                Lobby = new CoopLobby(SteamNative, SharedClock);
                Lobby.JoinerAppeared += id =>
                {
                    var t = HostSteamTransport;
                    if (t != null && t.AddPeer(id)) Logger.LogInfo($"co-op: friend '{SteamNative.FriendName(id)}' asked to join, letting them in");
                };
                Lobby.JoinerGone += id => HostSteamTransport?.RemovePeer(id);
            }
            return ((ISteamSocial)SteamNative).Ready;
        }

        /// <summary>Host a game for Steam friends. Friends who have the game open see it in their co-op list.</summary>
        internal static void StartHostSteam()
        {
            if (!EnsureSteam()) { Logger.LogWarning("co-op: Steam is not ready yet, cannot host"); return; }
            StopCoop();
            var t = SteamTransport.ForHost(SteamNative, SharedClock, new ulong[0]);
            HostSteamTransport = t;
            TransportDiag = t;
            AttachSession(new CoopSession(SessionRole.Host, t, SharedClock, SteamNative.LocalName));
            Lobby.StartHosting();
            Logger.LogInfo($"co-op: hosting over Steam as '{Session.LocalName}'; friends can join from their co-op list");
        }

        /// <summary>Join a friend's hosted game.</summary>
        internal static void StartJoinSteam(ulong hostId)
        {
            if (!EnsureSteam()) { Logger.LogWarning("co-op: Steam is not ready yet, cannot join"); return; }
            StopCoop();
            var t = SteamTransport.ForClient(SteamNative, SharedClock, hostId);
            TransportDiag = t;
            AttachSession(new CoopSession(SessionRole.Client, t, SharedClock, SteamNative.LocalName));
            Lobby.StartJoining(hostId);
            Logger.LogInfo($"co-op: joining '{SteamNative.FriendName(hostId)}' over Steam as '{Session.LocalName}'");
        }

        /// <summary>Leave or stop hosting: ends the session, clears the Steam presence, puts shared bodies back under the game's physics.</summary>
        internal static void StopCoop()
        {
            try { Lobby?.Stop(); } catch (Exception e) { Logger.LogWarning($"co-op: lobby stop failed: {e.Message}"); }
            try { Session?.Dispose(); } catch (Exception e) { Logger.LogWarning($"co-op: session dispose failed: {e.Message}"); }
            try { Bodies?.Shutdown(); } catch (Exception e) { Logger.LogWarning($"co-op: body shutdown failed: {e.Message}"); }
            Session = null;
            Bodies = null;
            Events = null;
            HostSteamTransport = null;
            TransportDiag = null;
        }

        public override void Load()
        {
            Logger = Log;
            Logger.LogInfo($"{PluginName} {PluginVersion} loading");

            // Everything is off unless the config asks for it, so the mod cannot change normal play by accident.
            var role = Config.Bind("Net", "Role", "Off", "Off, Host or Client. Off = the mod only logs and does nothing else.");
            var port = Config.Bind("Net", "Port", 47770, "UDP port the host listens on / the client connects to.");
            var bind = Config.Bind("Net", "BindAddress", "127.0.0.1", "Host only: address to listen on. 127.0.0.1 keeps it on this PC.");
            var hostAddr = Config.Bind("Net", "HostAddress", "127.0.0.1", "Client only: address of the host.");
            var name = Config.Bind("Net", "PlayerName", "Player", "Name shown to the other players.");
            var poseHz = Config.Bind("Net", "PoseHz", 60, "How many times per second your head and hands are sent (10-120).");
            PoseHz = Math.Max(10, Math.Min(120, poseHz.Value));
            var transport = Config.Bind("Net", "Transport", "Udp", "Udp (same PC / LAN, address and port below) or Steam (friends over the internet, Steam IDs below).");
            var hostSteamId = Config.Bind("Steam", "HostSteamId", "", "Client only, Transport=Steam: the host's Steam ID (a 17-digit number the host sees in its log).");
            var allowedSteamIds = Config.Bind("Steam", "AllowedSteamIds", "", "Host only, Transport=Steam: Steam IDs of the friends allowed to join, separated by commas. Nobody else gets through.");
            var bot = Config.Bind("Test", "Bot", false, "Send synthetic poses instead of real tracking and verify the poses received.");
            var quit = Config.Bind("Test", "AutoQuitSeconds", 0, "If above 0: print the test result and quit the game after this many seconds.");
            var selfProbe = Config.Bind("Test", "SteamSelfProbe", false, "Check the Steam binding without contacting anyone: wait for Steam, log your Steam ID, try one packet to your own ID.");
            var guard = Config.Bind("Test", "SaveGuard", false, "Test only: block every game write of progress, saves, Steam stats and achievements (they belong to the real account).");
            var bodyBot = Config.Bind("Test", "BodyBot", false, "Test only, needs a chapter scene and SaveGuard: stand-in players that push doors, to test door sync without a headset.");
            BodyBot = bodyBot.Value;
            WaterProbe = Config.Bind("Test", "WaterProbe", false, "Test only, needs a chapter scene and SaveGuard: drop a synthetic player through a water surface and count the splashes.").Value;
            ForceVrPanel = Config.Bind("Test", "ForceVrPanel", false, "Test only: build the VR world-space panel even without a headset.").Value;
            EventBot = Config.Bind("Test", "EventBot", false, "Test only: fire a few trigger events and count the replays on the other machine.").Value;
            AutoFollowHost = Config.Bind("Co-op", "AutoFollowHost", false, "If true a client loads the chapter the host starts without asking. Off by default: starting a chapter from the menu can reset that chapter's autosave.").Value;
            var bodyBotBase = Config.Bind("Test", "BodyBotBaseUnixMs", 0L, "Test only: shared start time (unix ms) so two processes push doors at the same moment.");
            BodyBotBase = bodyBotBase.Value;
            var recon = Config.Bind("Test", "Recon", false, "Test only: write a survey of the main menu scene to BepInEx\\recon.");
            var reconScene = Config.Bind("Test", "ReconLoadScene", "", "Test only, needs SaveGuard: after the survey, load this scene and survey it too.");
            BotEnabled = bot.Value;
            AutoQuitSeconds = quit.Value;
            SaveGuard = guard.Value;
            ReconEnabled = recon.Value;
            ReconLoadScene = (reconScene.Value ?? "").Trim();
            if (SaveGuard) Logger.LogInfo("co-op: SaveGuard is ON (game writes of progress, stats and achievements are blocked)");
            if (selfProbe.Value)
            {
                SteamProbe = new SteamP2PNative();
                Logger.LogInfo("co-op: Steam self probe enabled (contacts nobody but this account)");
            }

            try
            {
                string r = (role.Value ?? "Off").Trim().ToLowerInvariant();
                bool steam = string.Equals((transport.Value ?? "Udp").Trim(), "Steam", StringComparison.OrdinalIgnoreCase);
                var clock = new StopwatchClock();
                if (r == "host")
                {
                    ITransport t;
                    if (steam)
                    {
                        List<ulong> allowed = ParseIds(allowedSteamIds.Value);
                        if (allowed.Count == 0) throw new InvalidOperationException("Transport=Steam on the host needs [Steam] AllowedSteamIds (the friends who may join)");
                        var st = SteamTransport.ForHost(new SteamP2PNative(), clock, allowed);
                        TransportDiag = st;
                        t = st;
                        Logger.LogInfo($"co-op: HOST over Steam, {st.PeerCount} allowed friend(s); waiting for Steam to be ready");
                    }
                    else
                    {
                        t = UdpTransport.Listen(IPAddress.Parse(bind.Value), port.Value);
                        Logger.LogInfo($"co-op: HOST listening on {bind.Value}:{port.Value}");
                    }
                    Session = new CoopSession(SessionRole.Host, t, clock, name.Value);
                    Logger.LogInfo($"co-op: HOST as '{Session.LocalName}'");
                }
                else if (r == "client")
                {
                    ITransport t;
                    if (steam)
                    {
                        List<ulong> ids = ParseIds(hostSteamId.Value);
                        if (ids.Count != 1) throw new InvalidOperationException("Transport=Steam on a client needs exactly one [Steam] HostSteamId");
                        var st = SteamTransport.ForClient(new SteamP2PNative(), clock, ids[0]);
                        TransportDiag = st;
                        t = st;
                        Logger.LogInfo("co-op: CLIENT over Steam; waiting for Steam to be ready");
                    }
                    else
                    {
                        t = UdpTransport.Connect(new IPEndPoint(IPAddress.Parse(hostAddr.Value), port.Value));
                        Logger.LogInfo($"co-op: CLIENT connecting to {hostAddr.Value}:{port.Value}");
                    }
                    Session = new CoopSession(SessionRole.Client, t, clock, name.Value);
                    Logger.LogInfo($"co-op: CLIENT as '{Session.LocalName}'");
                }
                else
                {
                    Logger.LogInfo("co-op: Role=Off (set [Net] Role to Host or Client in the config to enable)");
                }
            }
            catch (Exception e)
            {
                Logger.LogError($"co-op: could not start the session: {e}");
                Session = null;
            }

            if (Session != null) AttachSession(Session);
            ShowPanelAtStart = Config.Bind("Test", "ShowPanelAtStart", false, "Test only: open the co-op panel as soon as the game runs.").Value;
            AutoHost = Config.Bind("Test", "AutoHost", false, "Test only: press Host a game by itself 12 s after start and Stop hosting 14 s later.").Value;
            ShotAtSeconds = Config.Bind("Test", "ShotAtSeconds", 0, "Test only, needs a visible game window: save a screenshot after this many seconds.").Value;

            ClassInjector.RegisterTypeInIl2Cpp<CoopBehaviour>();
            var go = new GameObject("PoolsCoop");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<CoopBehaviour>();

            try
            {
                SceneManager.sceneLoaded += (UnityEngine.Events.UnityAction<Scene, LoadSceneMode>)((scene, mode) =>
                {
                    Logger.LogInfo($"scene loaded: '{scene.name}' mode={mode} buildIndex={scene.buildIndex}");
                    if (mode == LoadSceneMode.Single && Session != null && Session.Role == SessionRole.Host)
                        Session.SetLocalScene(scene.name);
                });
            }
            catch (Exception e)
            {
                Logger.LogWarning($"could not hook sceneLoaded: {e.Message}");
            }

            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(Plugin).Assembly);
                Logger.LogInfo("Harmony patches applied");
            }
            catch (Exception e)
            {
                Logger.LogError($"Harmony patching failed: {e}");
            }
        }
    }

    /// <summary>Logs (does not change) how the game starts a chapter.</summary>
    [HarmonyPatch(typeof(Backrooms.Scripts.SceneLoader), nameof(Backrooms.Scripts.SceneLoader.Load))]
    internal static class SceneLoaderLoadLog
    {
        private static void Prefix(Backrooms.Features.Levels.Level level, bool shouldLoadAutoSave)
        {
            string scene = level != null ? level.scene : "<null>";
            Plugin.Logger.LogInfo($"SceneLoader.Load(level.scene='{scene}', loadAutoSave={shouldLoadAutoSave})");
        }
    }
}
