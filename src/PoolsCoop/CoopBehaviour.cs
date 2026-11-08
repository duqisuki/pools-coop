using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using PoolsCoop.Core;
using PoolsCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoolsCoop
{
    /// <summary>Runs on the game's main thread: pumps the session, sends the local pose, drives puppets for the remote players.</summary>
    public class CoopBehaviour : MonoBehaviour
    {
        private readonly Dictionary<byte, Puppet> _puppets = new Dictionary<byte, Puppet>();
        private readonly List<float> _puppetErrs = new List<float>(4096); // drawn pose vs the bot's true path, metres
        private float _t0 = -1f;
        private float _lastNow = -1f;
        private float _maxFrame;       // longest real frame, seconds
        private float _maxUnscaledDt;  // longest Time.unscaledDeltaTime the game reported, seconds
        private float _lastDelayMs;
        private float _nextSend;
        private float _nextLog;
        private bool _loggedSwitcher;
        private bool _resultLogged;

        public CoopBehaviour(IntPtr ptr) : base(ptr) { }

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (_t0 < 0f) _t0 = now;
            // The game may cap Time.unscaledDeltaTime (Time.maximumDeltaTime), which would make puppets crawl after a hitch.
            // Real elapsed time between our own updates is not capped.
            float frame = _lastNow < 0f ? 0f : now - _lastNow;
            _lastNow = now;
            if (frame > _maxFrame) _maxFrame = frame;
            if (Time.unscaledDeltaTime > _maxUnscaledDt) _maxUnscaledDt = Time.unscaledDeltaTime;

            try { UpdateSteamProbe(now); }
            catch (Exception e) { Plugin.Logger.LogWarning($"steam probe failed: {e.Message}"); _probeStage = 99; }

            try { UpdateRecon(now); }
            catch (Exception e) { Plugin.Logger.LogWarning($"recon failed: {e}"); _reconStage = 9; }

            try { PuppetWater.RunProbe(now); }
            catch (Exception e) { Plugin.Logger.LogWarning($"water probe failed: {e}"); }

            try { Plugin.Lobby?.Update(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"lobby update failed: {e.Message}"); }

            try { Plugin.TickFollow(now); }
            catch (Exception e) { Plugin.Logger.LogWarning($"follow failed: {e.Message}"); Plugin.FollowScene = ""; }

            try { HandleKeysAndShot(now); }
            catch (Exception e) { Plugin.Logger.LogWarning($"panel input failed: {e.Message}"); }
            TickPanel();

            var s = Plugin.Session;
            if (s == null && _puppets.Count > 0)
            {
                foreach (var p in _puppets.Values) p.Destroy(); // the session ended: remove the friends' puppets
                _puppets.Clear();
            }
            if (s != null)
            {
                try { s.Update(); }
                catch (Exception e) { Plugin.Logger.LogWarning($"session update failed: {e.Message}"); }

                if (now >= _nextSend)
                {
                    _nextSend = now + 1f / Math.Max(10, Plugin.PoseHz);
                    TrySendLocalPose(s, now);
                }

                try { UpdatePuppets(s, frame, now); }
                catch (Exception e) { Plugin.Logger.LogWarning($"puppet update failed: {e.Message}"); }

                if (Plugin.Events != null)
                {
                    try { Plugin.Events.Update(s, now); UpdateEventBot(s, now); FxSync.RunBot(s, now); }
                    catch (Exception e) { Plugin.Logger.LogWarning($"event sync failed: {e}"); }
                }

                if (Plugin.Bodies != null)
                {
                    try { Plugin.Bodies.Update(s, now, frame); Plugin.Bodies.UpdateBot(s, now); }
                    catch (Exception e) { Plugin.Logger.LogWarning($"body sync failed: {e}"); }
                }
            }

            if (now >= _nextLog)
            {
                _nextLog = now + 5f;
                try { LogStatus(s); }
                catch (Exception e) { Plugin.Logger.LogWarning($"status log failed: {e.Message}"); }
            }

            if (Plugin.AutoQuitSeconds > 0 && !_resultLogged && now - _t0 >= Plugin.AutoQuitSeconds)
            {
                _resultLogged = true;
                try { if (s != null && Plugin.Bodies != null) Plugin.Bodies.LogFinal(s); } catch (Exception e) { Plugin.Logger.LogWarning($"body final log failed: {e.Message}"); }
                try
                {
                    var ev = Plugin.Events;
                    if (s != null && ev != null)
                        Plugin.Logger.LogInfo($"EVENT-STATS hooked={ev.Hooked} localFired={ev.LocalFired} replayed={ev.Replayed} skippedEcho={ev.SkippedEcho} unknownId={ev.UnknownId} sent={s.GameEventsSent} received={s.GameEventsReceived}");
                }
                catch (Exception e) { Plugin.Logger.LogWarning($"event final log failed: {e.Message}"); }
                Plugin.Logger.LogInfo($"FX-STATS botCalls={FxSync.BotCalls} botErrors={FxSync.BotErrors} sentSteps={FxSync.SentSteps} sentSplashes={FxSync.SentSplashes} replayedSteps={FxSync.ReplayedSteps} replayedSplashes={FxSync.ReplayedSplashes} failures={FxSync.Failures} ripples={PuppetWater.RipplesRequested} particles={PuppetWater.ParticlesSpawned} sounds={PuppetWater.SoundsPlayed} wakes={PuppetWater.Wakes} waterErrors={PuppetWater.Errors}");
                LogTestResult(s);
                Application.Quit();
            }
        }

        // ---- panel ---------------------------------------------------------------------------------

        private bool _panelInit;
        private bool _shotTaken;

        private void HandleKeysAndShot(float now)
        {
            if (!_panelInit)
            {
                _panelInit = true;
                if (Plugin.ShowPanelAtStart) CoopPanel.SetVisible(true, "test switch");
                Plugin.Logger.LogInfo("co-op: press F7 (or F8), or the CO-OP button in the menu, for the co-op panel");
            }

            if (Plugin.AutoHost) UpdateAutoHost(now);


            CoopPanel.HandleKeys();

            if (Plugin.ShotAtSeconds > 0 && !_shotTaken && now - _t0 >= Plugin.ShotAtSeconds)
            {
                _shotTaken = true;
                string path = System.IO.Path.Combine(Recon.OutDir, "screenshot.png");
                ScreenCapture.CaptureScreenshot(path);
                Plugin.Logger.LogInfo($"screenshot requested: {path}");
            }
        }

        // ---- event bot (test only) -----------------------------------------------------------------

        private readonly HashSet<string> _eventBotDone = new HashSet<string>();

        /// <summary>
        /// Stand-in for players walking into scary triggers: at the same shared wall-clock moment the host fires four enter-type events
        /// and the client fires four others, in the same chapter. Each machine then reports how many it fired and how many it replayed.
        /// </summary>
        [HideFromIl2Cpp]
        private void UpdateEventBot(CoopSession s, float now)
        {
            if (!Plugin.EventBot || !s.Connected || Plugin.Events == null || Plugin.Events.Hooked < 10 || Plugin.BodyBotBase <= 0) return;
            float t = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Plugin.BodyBotBase) / 1000f;
            var ids = Plugin.Events.SortedUnityEventIds();
            if (ids.Count < 8) return;
            bool host = s.Role == SessionRole.Host;
            if (t > 14f && !_eventBotDone.Contains("fire"))
            {
                _eventBotDone.Add("fire");
                for (int i = 0; i < 4; i++) Plugin.Events.BotFire(ids[host ? i : 4 + i]);
                Plugin.Logger.LogInfo($"events bot: {(host ? "host" : "client")} fired 4 events");
            }
        }

        private int _autoHostStage;
        private float _autoHostAt;

        /// <summary>Test hook: the same calls the panel's buttons make, with a check of the Steam presence in between.</summary>
        private void UpdateAutoHost(float now)
        {
            if (_autoHostStage >= 3 || now - _t0 < 12f) return;
            var soc = Plugin.SteamNative as ISteamSocial;
            if (_autoHostStage == 0)
            {
                if (!Plugin.EnsureSteam()) return;
                Plugin.StartHostSteam();
                _autoHostAt = now;
                _autoHostStage = 1;
                return;
            }
            if (_autoHostStage == 1 && now - _autoHostAt > 8f)
            {
                string host = soc.GetRich(soc.LocalId, CoopLobby.KeyHost);
                string status = soc.GetRich(soc.LocalId, CoopLobby.KeyStatus);
                var s = Plugin.Session;
                Plugin.Logger.LogInfo($"AUTO-HOST hosting: sessionRole={(s == null ? "none" : s.Role.ToString())} presenceHostIsOwnId={host == soc.LocalId.ToString()} status='{status}' lobbyState={Plugin.Lobby.State}");
                _autoHostStage = 2;
                return;
            }
            if (_autoHostStage == 2 && now - _autoHostAt > 14f)
            {
                Plugin.StopCoop();
                string host = soc.GetRich(soc.LocalId, CoopLobby.KeyHost);
                string status = soc.GetRich(soc.LocalId, CoopLobby.KeyStatus);
                Plugin.Logger.LogInfo($"AUTO-HOST stopped: session={(Plugin.Session == null ? "none" : "STILL THERE")} presenceHost='{host}' status='{status}' lobbyState={Plugin.Lobby.State}");
                _autoHostStage = 3;
            }
        }

        private bool _panelFailedLogged;

        private void TickPanel()
        {
            try { CoopPanel.Tick(); }
            catch (Exception e)
            {
                if (!_panelFailedLogged) { _panelFailedLogged = true; Plugin.Logger.LogWarning($"panel failed: {e}"); }
            }
        }

        // ---- recon (test only) ---------------------------------------------------------------------

        private int _reconStage;
        private float _reconAt;

        private void UpdateRecon(float now)
        {
            if (!Plugin.ReconEnabled || _reconStage >= 4) return;
            string active = SceneManager.GetActiveScene().name;

            if (_reconStage == 0)
            {
                if (now - _t0 < 6f || active == "loadMainMenu" || BodySync.IsGameplayScene(active)) return; // wait for the menu scene (its name differs between the VR and flat builds)
                Recon.LogBuildScenes();
                Recon.DumpScene("menu");
                _reconStage = Plugin.ReconLoadScene.Length > 0 ? 1 : 4;
                return;
            }

            if (_reconStage == 1)
            {
                // Never load a chapter unless the guard has been shown to work: call a guarded static method with a made-up
                // stat name and check that the prefix ran. (If it did not, the real method would try a harmless unknown stat.)
                if (!Plugin.SaveGuard) { Plugin.Logger.LogWarning("recon: SaveGuard is off, not loading a scene"); _reconStage = 4; return; }
                int before = GuardCounter.Blocked;
                Backrooms.Features.External.ExternalManagers.TryUpdateStat("COOP_GUARD_SELFTEST", 0);
                if (GuardCounter.Blocked != before + 1)
                {
                    Plugin.Logger.LogError("recon: GUARD SELF TEST FAILED (the patch did not run); not loading a scene");
                    _reconStage = 4;
                    return;
                }
                Plugin.Logger.LogInfo($"recon: guard self test passed ({GuardCounter.Last}); loading '{Plugin.ReconLoadScene}'");
                SceneManager.LoadScene(Plugin.ReconLoadScene);
                _reconAt = now;
                _reconStage = 2;
                return;
            }

            if (_reconStage == 2)
            {
                if (active == Plugin.ReconLoadScene && now - _reconAt > 12f)
                {
                    Recon.DumpScene("chapter");
                    _reconAt = now;
                    _reconStage = 3;
                }
                return;
            }

            if (_reconStage == 3 && now - _reconAt > 1f)
            {
                Plugin.Logger.LogInfo($"recon: done; guard blocked {GuardCounter.Blocked} write attempt(s), last '{GuardCounter.Last}'");
                _reconStage = 4;
            }
        }

        // ---- Steam self probe ----------------------------------------------------------------------

        private int _probeStage;
        private float _probeSentAt;

        /// <summary>Checks the native Steam binding with no other person involved: it only ever sends to this account's own ID.</summary>
        [HideFromIl2Cpp]
        private void UpdateSteamProbe(float now)
        {
            var p = Plugin.SteamProbe;
            if (p == null || _probeStage >= 2) return;
            if (!p.Ready) return; // the binding logs once it is ready

            if (_probeStage == 0)
            {
                bool sent = p.Send(p.LocalId, new byte[] { 0x50, 0x4C, 1, 0xFE, 1, 2, 3, 4 });
                Plugin.Logger.LogInfo($"steam self probe: send to own ID -> {sent}; {p.Describe(p.LocalId)}");

                // Friends and rich presence: counts only, and a round trip of one made-up key on our own presence.
                ISteamSocial soc = p;
                if (!soc.Ready) Plugin.Logger.LogInfo("steam self probe: friends API not available");
                else
                {
                    var friends = soc.Friends();
                    int inGame = 0;
                    foreach (var f in friends) if (soc.FriendInThisGame(f)) inGame++;
                    soc.SetRich("poolscoop_probe", "ok");
                    string back = soc.GetRich(p.LocalId, "poolscoop_probe");
                    soc.SetRich("poolscoop_probe", "");
                    string cleared = soc.GetRich(p.LocalId, "poolscoop_probe");
                    Plugin.Logger.LogInfo($"steam self probe: friends={friends.Count} inThisGame={inGame} richPresenceRoundTrip='{back}' afterClear='{cleared}' personaNameLength={soc.LocalName.Length}");
                }
                _probeSentAt = now;
                _probeStage = 1;
                return;
            }

            if (p.TryReceive(out ulong from, out byte[] data))
            {
                Plugin.Logger.LogInfo($"steam self probe: RECEIVED {data.Length} bytes back from {(from == p.LocalId ? "own ID" : "another ID")}");
                _probeStage = 2;
            }
            else if (now - _probeSentAt > 4f)
            {
                Plugin.Logger.LogInfo($"steam self probe: nothing came back within 4 s; {p.Describe(p.LocalId)}");
                _probeStage = 2;
            }
        }

        // ---- local pose ----------------------------------------------------------------------------

        private static void TrySendLocalPose(CoopSession s, float now)
        {
            uint tms = (uint)(now * 1000f);
            if (Plugin.BotEnabled)
            {
                if (s.Connected) s.SendLocalPose(BotPose.At(tms, s.LocalId), tms);
                return;
            }

            // VR: the head camera and the game's own physics hands with their finger bends; flat screen: the camera and natural hands.
            PlayerPose pose;
            bool ok = Plugin.IsVrActive ? LocalRig.TryVr(now, out pose) : LocalRig.TryFlat(now, out pose);
            if (ok) s.SendLocalPose(pose, tms);
        }

        /// <summary>The camera the flat-screen player looks through.</summary>
        internal static Camera FlatCamera()
        {
            try
            {
                Camera m = Camera.main;
                if (m != null && m.enabled && m.gameObject.activeInHierarchy) return m;
                foreach (var c in Camera.allCameras)
                    if (c != null && c.enabled && c.gameObject.activeInHierarchy && c.cameraType == CameraType.Game) return c;
            }
            catch (Exception) { }
            return null;
        }

        private static Vec3 ToVec(Vector3 v) => new Vec3(v.x, v.y, v.z);
        private static Quat ToQuat(Quaternion q) => new Quat(q.x, q.y, q.z, q.w);

        [HideFromIl2Cpp]
        private int RealHandPuppets()
        {
            int n = 0;
            foreach (var p in _puppets.Values) if (p.HasRealHands) n++;
            return n;
        }

        private static NetPose FromTransform(Transform t)
        {
            Vector3 p = t.position;
            Quaternion q = t.rotation;
            return new NetPose(new Vec3(p.x, p.y, p.z), new Quat(q.x, q.y, q.z, q.w));
        }

        // ---- remote players ------------------------------------------------------------------------

        [HideFromIl2Cpp]
        private void UpdatePuppets(CoopSession s, float dt, float now)
        {
            List<byte> stale = null;
            foreach (var id in _puppets.Keys)
                if (!s.Remotes.ContainsKey(id)) { if (stale == null) stale = new List<byte>(); stale.Add(id); }
            if (stale != null)
                foreach (var id in stale) { _puppets[id].Destroy(); _puppets.Remove(id); }

            foreach (var kv in s.Remotes)
            {
                RemotePlayer rp = kv.Value;
                if (!rp.HasPose) continue;
                if (!_puppets.TryGetValue(rp.Id, out Puppet pup))
                {
                    pup = new Puppet(rp.Id, rp.Name);
                    _puppets[rp.Id] = pup;
                }
                if (!rp.Buffer.Sample(s.NowMs, out PlayerPose drawn)) continue;
                pup.Apply(drawn);
                pup.WaterTick(drawn, now);
                _lastDelayMs = (float)rp.Buffer.DelayMs;

                // Bot test only: the sender's path is known exactly, so measure how far the drawn puppet is from where the sender
                // really was at the moment the buffer is showing.
                if (Plugin.BotEnabled && now - _t0 > 5f && rp.Buffer.Count >= 20)
                {
                    PlayerPose truth = BotPose.At((uint)Math.Round(rp.Buffer.LastRenderSenderMs), rp.Id);
                    _puppetErrs.Add(pup.MaxDistanceTo(truth));
                }
            }
        }

        // ---- logging -------------------------------------------------------------------------------

        [HideFromIl2Cpp]
        private void LogStatus(CoopSession s)
        {
            bool vrMgr = Backrooms.Features.VR.PoolsVRManager.IsInstanced;
            bool xr = Backrooms.Features.VR.PoolsXROrigin.IsInstanced;
            bool fps = FPSController.IsInstanced;
            string scene = SceneManager.GetActiveScene().name;
            string net = s == null
                ? "net=off"
                : $"net={s.Role} connected={s.Connected} id={s.LocalId} remotes={s.Remotes.Count} in={s.PacketsIn} out={s.PacketsOut} bad={s.BadPackets} rtt={s.RttMs:F1}ms hostScene='{s.HostScene}'";
            Plugin.Logger.LogInfo(FormattableString.Invariant(
                $"status: scene='{scene}' PoolsVRManager={vrMgr} PoolsXROrigin={xr} FPSController={fps} {net} puppets={_puppets.Count}"));
            if (Plugin.TransportDiag != null) Plugin.Logger.LogInfo("transport: " + Plugin.TransportDiag.Describe());

            if (!_loggedSwitcher)
            {
                _loggedSwitcher = true;
                try
                {
                    Plugin.Logger.LogInfo($"VRModeSwitcher paths: vr='{Backrooms.Features.VR.VRModeSwitcher.urpVRBuildApplicationPath}' flat='{Backrooms.Features.VR.VRModeSwitcher.flatScreenBuildApplicationPath}'");
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"could not read VRModeSwitcher paths: {e.Message}");
                }
            }
        }

        [HideFromIl2Cpp]
        private void LogTestResult(CoopSession s)
        {
            if (s == null)
            {
                Plugin.Logger.LogInfo("COOP-TEST-RESULT role=Off verdict=SKIPPED");
                return;
            }
            int remotesWithPose = 0;
            int posesIn = 0;
            foreach (var rp in s.Remotes.Values)
            {
                if (rp.HasPose) remotesWithPose++;
                posesIn += rp.PosesReceived;
            }
            _puppetErrs.Sort();
            float p50 = _puppetErrs.Count > 0 ? _puppetErrs[_puppetErrs.Count / 2] : float.NaN;
            float p95 = _puppetErrs.Count > 0 ? _puppetErrs[(int)(0.95 * (_puppetErrs.Count - 1))] : float.NaN;
            float max = _puppetErrs.Count > 0 ? _puppetErrs[_puppetErrs.Count - 1] : float.NaN;
            bool pass = s.Connected
                        && remotesWithPose >= 1
                        && Plugin.PoseOk >= 50
                        && Plugin.PoseBad == 0
                        && s.BadPackets == 0
                        && _puppets.Count >= 1
                        && _puppetErrs.Count >= 100
                        && p95 < 0.010f;
            Plugin.Logger.LogInfo(FormattableString.Invariant(
                $"COOP-TEST-RESULT role={s.Role} id={s.LocalId} realHands={RealHandPuppets()} handTemplates={HandTemplate.Captured} fxSent={s.FxSent} fxReceived={s.FxReceived} stepsReplayed={FxSync.ReplayedSteps} splashesReplayed={FxSync.ReplayedSplashes} connected={s.Connected} remotesWithPose={remotesWithPose} posesAccepted={posesIn} poseOk={Plugin.PoseOk} poseBad={Plugin.PoseBad} puppets={_puppets.Count} puppetErrMm(p50/p95/max)={p50 * 1000f:F1}/{p95 * 1000f:F1}/{max * 1000f:F0} frames={_puppetErrs.Count} bufferDelayMs={_lastDelayMs:F0} visualLatencyMs~{s.RttMs / 2 + _lastDelayMs:F0} maxFrameMs={_maxFrame * 1000f:F0} badPackets={s.BadPackets} rejectedClaims={s.RejectedPoseClaims} strayPackets={s.StrayPackets} rttMs={s.RttMs:F1} hostScene='{s.HostScene}' verdict={(pass ? "PASS" : "FAIL")}"));
        }
    }
}
