using System;
using System.Collections.Generic;
using System.Text;
using PoolsCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoolsCoop
{
    /// <summary>
    /// Shares the physical objects of a chapter that players can move: hinged doors and floating rubber ducks. The survey of the
    /// chapters (local\recon) showed that these, plus chairs, are the only loose physics bodies; there is no pile of props.
    /// Whoever touches a body with head or hands claims it; the owner simulates it and streams its state, everyone else shows it
    /// kinematically. At rest the owner releases it and every machine simulates it again from the same resting state.
    /// Plain C# (not an injected Unity type); driven by CoopBehaviour.Update on the main thread.
    /// </summary>
    internal sealed class BodySync
    {
        // "At rest" must be strict: a hinged door still creeping at a few degrees per second would be released, and then every machine
        // would simulate its own slightly different creep.
        private const float MovingSpeed = 0.01f;
        private const float MovingSpin = 0.01f;
        private const float RestBeforeReleaseS = 1.5f;
        private const float ClaimCooldownS = 0.6f;
        private const float RescanEveryS = 5f;
        // AutoClaim thresholds: clearly moving, for a moment, and not while the scene is still settling after the load.
        private const float AutoClaimSpeed = 0.15f;
        private const float AutoClaimSpin = 0.4f;
        private const float AutoClaimAfterS = 0.2f;
        private const float SettleAfterLoadS = 4f;
        private float _sceneSince;
        public int AutoClaims;

        private sealed class Synced
        {
            public uint Id;
            public string Path;
            public Rigidbody Rb;
            public Collider[] Cols;
            public bool OrigKinematic;
            public bool Follower;
            public float RestSince = -1f;
            public float NextSend;
            public float NextClaim;
            public float MovingSince = -1f;
            public bool HasTarget;
            public Vector3 TPos, TVel, TAng;
            public Quaternion TRot;
            public float TRecvAt;
        }

        private readonly Dictionary<uint, Synced> _bodies = new Dictionary<uint, Synced>();
        private string _scene = "";
        private float _nextScan;
        private float _botT0 = -1f;
        private readonly HashSet<string> _botDone = new HashSet<string>();

        public int Count => _bodies.Count;
        public int Claims;
        public int Releases;
        public int StatesSent;
        public int FollowerApplies;

        // ---- scene bookkeeping ---------------------------------------------------------------------

        public static bool IsGameplayScene(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("menu", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("loading", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>Put every shared body back under the game's own physics (used when the co-op session ends).</summary>
        public void Shutdown() => Reset(null);

        private void Reset(CoopSession s)
        {
            foreach (var b in _bodies.Values)
            {
                try
                {
                    if (b.Rb != null) b.Rb.isKinematic = b.OrigKinematic;
                    if (s != null && s.IsBodyOwner(b.Id)) s.ReleaseBody(b.Id);
                }
                catch (Exception) { }
            }
            _bodies.Clear();
            _botT0 = -1f;
            _botDone.Clear();
            _nextScan = 0f;
        }

        /// <summary>Bodies that belong to a player (their body, hands, camera, head collider, photo camera): never shared as objects.</summary>
        private static bool IsPlayerPart(Rigidbody rb)
        {
            try
            {
                if (rb.GetComponent<Camera>() != null) return true;
                if (rb.GetComponent<FPSController>() != null) return true;
                if (rb.GetComponent<Autohand.Hand>() != null) return true;
                if (rb.GetComponent<Backrooms.Features.Player.PlayerBody>() != null) return true;
                string n = rb.gameObject.name;
                if (n == "HeadCollider" || n == "PhotoModeCamera" || n.Contains("AutoHand") || n.Contains("XR Origin") || n.Contains("Player")) return true;
                for (var t = rb.transform.parent; t != null; t = t.parent)
                    if (t.name.Contains("XR Origin") || t.name == "FPSController") return true;
            }
            catch (Exception) { return true; }
            return false;
        }

        private static uint Fnv(string text)
        {
            uint h = 2166136261u;
            foreach (char c in text) { h ^= c; h *= 16777619u; }
            return h;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name + "#" + x.GetSiblingIndex());
            parts.Reverse();
            return string.Join("/", parts);
        }

        private void Scan(string scene)
        {
            int added = 0;
            void Take(Rigidbody rb)
            {
                if (rb == null) return;
                string path = PathOf(rb.transform);
                uint id = Fnv(scene + "|" + path);
                if (_bodies.TryGetValue(id, out Synced existing))
                {
                    if (existing.Rb == null) _bodies.Remove(id); else return;
                }
                var cols = rb.GetComponentsInChildren<Collider>(true);
                _bodies[id] = new Synced { Id = id, Path = path, Rb = rb, Cols = cols, OrigKinematic = rb.isKinematic };
                added++;
            }

            foreach (var d in UnityEngine.Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Take(d.GetComponent<Rigidbody>());
            foreach (var b in UnityEngine.Object.FindObjectsByType<Backrooms.Features.Buoyancy.BuoyancyObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Take(b.GetComponent<Rigidbody>());

            // Every other loose physics body of the scene too. Almost all of them never move (they are interaction triggers on
            // furniture), and the ones that do are claimed by whoever sees them move first (see AutoClaim below).
            foreach (var rb in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (rb == null || rb.isKinematic) continue;
                if (IsPlayerPart(rb)) continue;
                Take(rb);
            }

            if (added > 0) Plugin.Logger.LogInfo($"bodies: {added} new shared body(ies) in scene '{scene}', {_bodies.Count} in total");
        }

        // ---- session events ------------------------------------------------------------------------

        public void OnOwnerChanged(BodyView v, CoopSession s)
        {
            if (!_bodies.TryGetValue(v.Id, out Synced b) || b.Rb == null) return;
            if (v.Owner == s.LocalId)
            {
                b.Follower = false;
                b.Rb.isKinematic = b.OrigKinematic;
                if (b.HasTarget) { b.Rb.linearVelocity = b.TVel; b.Rb.angularVelocity = b.TAng; }
                b.RestSince = -1f;
            }
            else if (v.Owner == BodyView.NoOwner)
            {
                b.Follower = false;
                b.Rb.isKinematic = b.OrigKinematic;
                b.HasTarget = false;
            }
            else
            {
                b.Follower = true;
                b.Rb.isKinematic = true;
            }
        }

        public void OnState(BodyView v, float now)
        {
            if (!_bodies.TryGetValue(v.Id, out Synced b)) return;
            BodyState st = v.State;
            bool first = !b.HasTarget;
            b.TPos = new Vector3(st.Pos.X, st.Pos.Y, st.Pos.Z);
            b.TRot = new Quaternion(st.Rot.X, st.Rot.Y, st.Rot.Z, st.Rot.W);
            b.TVel = new Vector3(st.Vel.X, st.Vel.Y, st.Vel.Z);
            b.TAng = new Vector3(st.AngVel.X, st.AngVel.Y, st.AngVel.Z);
            b.TRecvAt = now;
            b.HasTarget = true;
            if (first && b.Rb != null && b.Follower)
            {
                b.Rb.position = b.TPos;
                b.Rb.rotation = b.TRot;
            }
        }

        // ---- per-frame work ------------------------------------------------------------------------

        public void Update(CoopSession s, float now, float dt)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != _scene)
            {
                Reset(s);
                _scene = scene;
                _sceneSince = now;
            }
            if (!IsGameplayScene(scene) || !s.Connected) return;

            if (now >= _nextScan)
            {
                _nextScan = now + RescanEveryS;
                Scan(scene);
            }

            var touch = CollectTouchPoints();
            _held.Clear();
            if (Plugin.IsVrActive) { try { LocalRig.HeldBodies(_held); } catch (Exception) { } }
            foreach (var b in _bodies.Values)
            {
                if (b.Rb == null) continue;
                bool mine = s.IsBodyOwner(b.Id);
                bool pendingMine = s.Bodies.TryGetValue(b.Id, out BodyView view) && view.Owner == s.LocalId && !mine;
                bool held = IsHeld(b);
                if (held) b.RestSince = -1f; // a body in the hand is never released, even when held still

                if (!mine && !pendingMine && now >= b.NextClaim && (held || Touching(b, touch)))
                {
                    b.NextClaim = now + ClaimCooldownS;
                    Claims++;
                    s.ClaimBody(b.Id);
                    mine = s.IsBodyOwner(b.Id); // on the host a claim is granted at once
                }
                else if (!mine && !pendingMine && !b.Follower && now - _sceneSince > SettleAfterLoadS && now >= b.NextClaim)
                {
                    // AutoClaim: a body nobody owns started to move by itself (knocked by something, a script, buoyancy). Every
                    // machine sees it move; the first claim the host orders wins, the others become followers of its simulation.
                    bool movingNow = b.Rb.linearVelocity.magnitude > AutoClaimSpeed || b.Rb.angularVelocity.magnitude > AutoClaimSpin;
                    if (!movingNow) b.MovingSince = -1f;
                    else if (b.MovingSince < 0f) b.MovingSince = now;
                    else if (now - b.MovingSince > AutoClaimAfterS)
                    {
                        b.NextClaim = now + ClaimCooldownS;
                        b.MovingSince = -1f;
                        AutoClaims++;
                        s.ClaimBody(b.Id);
                        mine = s.IsBodyOwner(b.Id);
                    }
                }

                if (mine) OwnerTick(s, b, now);
                else if (b.Follower && b.HasTarget) FollowerTick(b, now, dt);
            }
        }

        private void OwnerTick(CoopSession s, Synced b, float now)
        {
            float speed = b.Rb.linearVelocity.magnitude;
            float spin = b.Rb.angularVelocity.magnitude;
            bool moving = speed > MovingSpeed || spin > MovingSpin;
            if (moving) b.RestSince = -1f;
            else if (b.RestSince < 0f) b.RestSince = now;

            float interval = moving ? 1f / 30f : 0.25f;
            if (now >= b.NextSend)
            {
                b.NextSend = now + interval;
                var st = new BodyState();
                Vector3 p = b.Rb.position, v = b.Rb.linearVelocity, w = b.Rb.angularVelocity;
                Quaternion q = b.Rb.rotation;
                st.Pos = new Vec3(p.x, p.y, p.z);
                st.Rot = new Quat(q.x, q.y, q.z, q.w);
                st.Vel = new Vec3(v.x, v.y, v.z);
                st.AngVel = new Vec3(w.x, w.y, w.z);
                s.SendBodyState(b.Id, st, (uint)(now * 1000f));
                StatesSent++;
            }

            if (!moving && b.RestSince >= 0f && now - b.RestSince >= RestBeforeReleaseS)
            {
                Releases++;
                s.ReleaseBody(b.Id);
                b.RestSince = -1f;
            }
        }

        private void FollowerTick(Synced b, float now, float dt)
        {
            float ahead = Mathf.Min(now - b.TRecvAt, 0.15f);
            Vector3 pos = b.TPos + b.TVel * ahead;
            float angle = b.TAng.magnitude * ahead;
            Quaternion rot = angle > 1e-5f ? Quaternion.AngleAxis(angle * Mathf.Rad2Deg, b.TAng.normalized) * b.TRot : b.TRot;
            float k = 1f - Mathf.Exp(-30f * Mathf.Max(dt, 0.001f));
            b.Rb.position = Vector3.Lerp(b.Rb.position, pos, k);
            b.Rb.rotation = Quaternion.Slerp(b.Rb.rotation, rot, k);
            FollowerApplies++;
        }

        // ---- touch ---------------------------------------------------------------------------------

        private readonly List<Rigidbody> _held = new List<Rigidbody>(2);

        private bool IsHeld(Synced b)
        {
            for (int i = 0; i < _held.Count; i++)
                if (_held[i] != null && _held[i].Pointer == b.Rb.Pointer) return true;
            return false;
        }

        private static List<KeyValuePair<Vector3, float>> CollectTouchPoints()
        {
            var pts = new List<KeyValuePair<Vector3, float>>(4);
            try
            {
                if (!Plugin.IsVrActive)
                {
                    // Flat screen: the player's body is a standing capsule, plus a reach point in front of the camera.
                    if (FPSController.IsInstanced)
                    {
                        Vector3 f = FPSController.Instance.transform.position;
                        pts.Add(new KeyValuePair<Vector3, float>(f + new Vector3(0f, 0.35f, 0f), 0.35f));
                        pts.Add(new KeyValuePair<Vector3, float>(f + new Vector3(0f, 0.95f, 0f), 0.35f));
                        pts.Add(new KeyValuePair<Vector3, float>(f + new Vector3(0f, 1.5f, 0f), 0.3f));
                    }
                    Camera cam = CoopBehaviour.FlatCamera();
                    if (cam != null) pts.Add(new KeyValuePair<Vector3, float>(cam.transform.position + cam.transform.forward * 0.7f, 0.25f));
                }
                else if (Backrooms.Features.VR.PoolsXROrigin.IsInstanced)
                {
                    var o = Backrooms.Features.VR.PoolsXROrigin.Instance;
                    var cam = o.xrOrigin != null ? o.xrOrigin.Camera : null;
                    if (cam != null) pts.Add(new KeyValuePair<Vector3, float>(cam.transform.position, 0.18f));
                    // the game's physics hands, where they really are (they stop at surfaces instead of passing through)
                    if (LocalRig.TryHandPositions(out Vector3 lh, out Vector3 rh))
                    {
                        pts.Add(new KeyValuePair<Vector3, float>(lh, 0.11f));
                        pts.Add(new KeyValuePair<Vector3, float>(rh, 0.11f));
                    }
                    if (o.leftMotionControllerTracking != null) pts.Add(new KeyValuePair<Vector3, float>(o.leftMotionControllerTracking.transform.position, 0.12f));
                    if (o.rightMotionControllerTracking != null) pts.Add(new KeyValuePair<Vector3, float>(o.rightMotionControllerTracking.transform.position, 0.12f));
                }
            }
            catch (Exception) { }
            return pts;
        }

        private static bool Touching(Synced b, List<KeyValuePair<Vector3, float>> pts)
        {
            if (pts.Count == 0 || b.Cols == null) return false;
            foreach (var pt in pts)
            {
                foreach (var c in b.Cols)
                {
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    Bounds bb = c.bounds;
                    bb.Expand(pt.Value * 2f);
                    if (!bb.Contains(pt.Key)) continue;
                    Vector3 cp = c.ClosestPoint(pt.Key);
                    if ((cp - pt.Key).sqrMagnitude <= pt.Value * pt.Value) return true;
                }
            }
            return false;
        }

        // ---- bot (test only) -----------------------------------------------------------------------

        /// <summary>
        /// Stand-in for two players pushing doors. The host swings door 0, the client swings door 1, and both grab door 2 at the same
        /// moment, pushing opposite ways, to force an ownership conflict. Nothing touches the real player's data.
        /// </summary>
        public void UpdateBot(CoopSession s, float now)
        {
            if (!Plugin.BodyBot || !s.Connected || !IsGameplayScene(_scene) || _bodies.Count < 3) return;
            if (_botT0 < 0f) { _botT0 = now; return; }
            // Two separate processes must act at the same moment, so the runner gives both the same wall-clock start.
            float t = Plugin.BodyBotBase > 0
                ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Plugin.BodyBotBase) / 1000f
                : now - _botT0;

            var ids = new List<uint>();
            foreach (var kv in _bodies)
                if (kv.Value.Path.Contains("Door") || kv.Value.Path.ToLowerInvariant().Contains("door")) ids.Add(kv.Key);
            ids.Sort();
            if (ids.Count < 3) return;
            bool host = s.Role == SessionRole.Host;

            void Push(string key, uint id, float spin)
            {
                if (_botDone.Contains(key)) return;
                _botDone.Add(key);
                var b = _bodies[id];
                s.ClaimBody(id);
                b.Rb.isKinematic = false;
                b.Rb.angularVelocity = new Vector3(0f, spin, 0f);
                Plugin.Logger.LogInfo($"bodies bot: {(host ? "host" : "client")} pushed door {id:X8} with spin {spin}");
            }

            if (host && t > 0f) Push("a", ids[0], 3f);
            if (!host && t > 3f) Push("b", ids[1], 3f);
            if (t > 8f) Push("c", ids[2], host ? 3f : -3f);

            // Two doors that start moving WITHOUT anyone claiming them (as if knocked by physics): AutoClaim has to notice.
            void Spin(string key, uint id, float spin)
            {
                if (_botDone.Contains(key) || ids.Count <= 4) return;
                _botDone.Add(key);
                var b = _bodies[id];
                b.Rb.isKinematic = false;
                b.Rb.angularVelocity = new Vector3(0f, spin, 0f);
                Plugin.Logger.LogInfo($"bodies bot: {(host ? "host" : "client")} set door {id:X8} spinning with no claim");
            }
            if (host && t > 12f && ids.Count > 3) Spin("d", ids[3], 3f);
            if (!host && t > 13f && ids.Count > 4) Spin("e", ids[4], 3f);
        }

        /// <summary>One line per bot door with its final orientation, so the test runner can compare the two machines.</summary>
        public void LogFinal(CoopSession s)
        {
            if (!Plugin.BodyBot) return;
            var ids = new List<uint>();
            foreach (var kv in _bodies)
                if (kv.Value.Path.ToLowerInvariant().Contains("door")) ids.Add(kv.Key);
            ids.Sort();
            for (int i = 0; i < Math.Min(5, ids.Count); i++)
            {
                var b = _bodies[ids[i]];
                Quaternion q = b.Rb.rotation;
                s.Bodies.TryGetValue(ids[i], out BodyView v);
                Plugin.Logger.LogInfo(FormattableString.Invariant(
                    $"BODY-FINAL idx={i} id={ids[i]:X8} q=({q.x:F4},{q.y:F4},{q.z:F4},{q.w:F4}) owner={(v == null ? -1 : v.Owner)} epoch={(v == null ? -1 : v.Epoch)} kinematic={b.Rb.isKinematic} follower={b.Follower}"));
            }
            Plugin.Logger.LogInfo(FormattableString.Invariant(
                $"BODY-STATS bodies={_bodies.Count} claims={Claims} autoClaims={AutoClaims} releases={Releases} statesSent={StatesSent} statesAccepted={s.BodyStatesAccepted} staleStates={s.StaleBodyStates} followerApplies={FollowerApplies}"));
        }
    }
}
