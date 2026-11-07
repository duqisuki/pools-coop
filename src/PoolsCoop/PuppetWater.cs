using System;
using System.Collections.Generic;
using PoolsCoop.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoolsCoop
{
    /// <summary>
    /// Water effects for a remote player. The game makes ripples and splashes from each local rigidbody (RigidbodyRippleSpawner),
    /// which a puppet is not, so the plugin does the same job for the puppet's head and hands: when a part crosses the water surface
    /// it asks the game's own RippleManager for a ripple and a template spawner for the splash particles. Sound is not reproduced
    /// (the game's splash audio plays at the spawner's own position, which would be the local player).
    /// </summary>
    internal sealed class PuppetWater
    {
        // Shared by all puppets: the water volumes of the scene and one template spawner to copy splash settings from.
        private static readonly List<WaterBodyVolume> Volumes = new List<WaterBodyVolume>();
        private static readonly List<Bounds> VolumeBounds = new List<Bounds>();
        private static string _volumesScene = "";
        private static float _nextScan;
        private static Backrooms.Features.WaterDeform.RigidbodyRippleSpawner _template;

        public static int Enters, Exits, RipplesRequested, ParticlesSpawned, Errors;
        public static int MaxActiveRipples;

        private struct PartState
        {
            public bool Has, Under;
            public Vector3 Prev;
            public float PrevT;
            public float NextEvent;
        }

        private readonly PartState[] _parts = new PartState[3];

        private static void Rescan(float now)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene == _volumesScene && now < _nextScan) return;
            _volumesScene = scene;
            _nextScan = now + 5f;
            Volumes.Clear();
            VolumeBounds.Clear();
            try
            {
                foreach (var v in UnityEngine.Object.FindObjectsByType<WaterBodyVolume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    var cols = v.GetComponentsInChildren<Collider>(false);
                    if (cols.Length == 0) continue;
                    Bounds b = cols[0].bounds;
                    for (int i = 1; i < cols.Length; i++) b.Encapsulate(cols[i].bounds);
                    b.Expand(1.0f);
                    Volumes.Add(v);
                    VolumeBounds.Add(b);
                }
                if (_template == null || _template.gameObject == null)
                {
                    foreach (var s in UnityEngine.Object.FindObjectsByType<Backrooms.Features.WaterDeform.RigidbodyRippleSpawner>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                        if (s.visualEffectPrefab != null) { _template = s; break; }
                }
            }
            catch (Exception e)
            {
                Errors++;
                Plugin.Logger.LogWarning($"water rescan failed: {e.Message}");
            }
        }

        /// <summary>Call every frame with the pose that is drawn (head, left, right).</summary>
        public void Tick(PlayerPose drawn, float now)
        {
            Rescan(now);
            if (Volumes.Count == 0) return;
            Check(0, drawn.Head.Pos, now);
            Check(1, drawn.Left.Pos, now);
            Check(2, drawn.Right.Pos, now);
        }

        private void Check(int i, Vec3 p, float now)
        {
            var pos = new Vector3(p.X, p.Y, p.Z);
            ref PartState st = ref _parts[i];

            WaterBodyVolume body = null;
            bool under = false;
            for (int k = 0; k < Volumes.Count; k++)
            {
                if (!VolumeBounds[k].Contains(pos)) continue;
                try
                {
                    if (Volumes[k].IsPointUnderWater(new Unity.Mathematics.float3(pos.x, pos.y, pos.z))) { body = Volumes[k]; under = true; break; }
                    body = Volumes[k]; // near this water but above the surface
                }
                catch (Exception) { Errors++; }
            }

            if (st.Has && body != null && under != st.Under && now >= st.NextEvent)
            {
                float dt = Mathf.Max(0.001f, now - st.PrevT);
                float speed = (pos - st.Prev).magnitude / dt;
                if (speed > 0.25f)
                {
                    st.NextEvent = now + 0.12f;
                    if (under) Enters++; else Exits++;
                    Splash(body, pos, speed);
                }
            }

            st.Has = true;
            st.Under = under;
            st.Prev = pos;
            st.PrevT = now;
        }

        private static void Splash(WaterBodyVolume body, Vector3 pos, float speed)
        {
            try
            {
                var s = _template;
                float size = s != null ? s.baseSize + s.sizePerVelocity * speed : 1.2f + 0.4f * speed;
                float rspeed = s != null ? s.baseSpeed + s.speedPerVelocity * speed : 2f;
                float amp = s != null ? s.baseAmplitude + s.amplitudePerVelocity * speed : 0.1f + 0.05f * speed;
                size = Mathf.Clamp(size, 0.3f, 4f);
                amp = Mathf.Clamp(amp, 0.02f, 0.6f);

                Unity.Mathematics.float3 surf = body.ProjectToWaterSurface(new Unity.Mathematics.float3(pos.x, pos.y, pos.z));
                var surface = new Vector3(surf.x, surf.y, surf.z);

                if (Backrooms.Features.WaterDeform.RippleManager.IsInstanced)
                {
                    // The game culls ripples far from the camera; the probe's water is far from the player, so it turns culling off.
                    Backrooms.Features.WaterDeform.RippleManager.Instance.TrySpawnRipple(surface, size, rspeed, amp, !_probeStartedOnce);
                    RipplesRequested++;
                    int active = Backrooms.Features.WaterDeform.RippleManager.Instance.activeRipples.Count;
                    if (active > MaxActiveRipples) MaxActiveRipples = active;
                }
                if (s != null && speed > 0.6f)
                {
                    s.SpawnRippleParticles(0.3f, surface, amp);
                    ParticlesSpawned++;
                }
            }
            catch (Exception e)
            {
                Errors++;
                if (Errors < 4) Plugin.Logger.LogWarning($"puppet splash failed: {e}");
            }
        }

        // ---- probe (test only) ---------------------------------------------------------------------

        private static bool _probeStartedOnce => _probeStart >= 0f;
        private static Puppet _probePuppet;
        private static PuppetWater _probeWater;
        private static float _probeStart = -1f;
        private static bool _probeDone;
        private static float _probeSurfaceY;
        private static Vector3 _probeXZ;
        private static string _probeBody = "";

        /// <summary>
        /// Drops a synthetic puppet through the surface of the first water body of the scene, down and up at 1.5 m/s, and counts what
        /// the splash code did. Proves the calls into the game's water code run and register ripples; it cannot show how they look.
        /// </summary>
        public static void RunProbe(float now)
        {
            if (!Plugin.WaterProbe || _probeDone) return;
            string scene = SceneManager.GetActiveScene().name;
            if (!BodySync.IsGameplayScene(scene)) return;
            Rescan(now);
            if (Volumes.Count == 0) return;

            if (_probeStart < 0f)
            {
                WaterBodyVolume w = Volumes[0];
                var cols = w.GetComponentsInChildren<Collider>(false);
                Vector3 c = cols[0].bounds.center;
                Unity.Mathematics.float3 s = w.ProjectToWaterSurface(new Unity.Mathematics.float3(c.x, c.y, c.z));
                _probeSurfaceY = s.y;
                _probeXZ = new Vector3(c.x, 0f, c.z);
                _probeBody = w.name;
                _probePuppet = new Puppet(201, "WaterProbe");
                _probeWater = new PuppetWater();
                _probeStart = now;
                Plugin.Logger.LogInfo($"water probe: body '{w.name}', surface y={_probeSurfaceY:F2}, {Volumes.Count} water bodies, template spawner {(_template != null ? "found" : "MISSING")}");
                return;
            }

            float t = now - _probeStart;
            if (t > 8f)
            {
                _probeDone = true;
                _probePuppet.Destroy();
                Plugin.Logger.LogInfo($"WATER-PROBE body='{_probeBody}' enters={Enters} exits={Exits} ripplesRequested={RipplesRequested} maxActiveRipples={MaxActiveRipples} particles={ParticlesSpawned} errors={Errors}");
                return;
            }

            // Up and down through the surface: y = surface + 0.6 * cos(1.2 t) -> crosses about every 1.3 s at about 0.7 m/s peak.
            float y = _probeSurfaceY + 0.8f * Mathf.Cos(2.4f * t);
            var pos = new Vec3(_probeXZ.x, y, _probeXZ.z);
            var q = new Quat(0, 0, 0, 1);
            var pose = new PlayerPose();
            pose.Head = new NetPose(pos, q);
            pose.Left = new NetPose(new Vec3(pos.X + 3f, 100f, pos.Z), q);  // hands far away and dry
            pose.Right = new NetPose(new Vec3(pos.X - 3f, 100f, pos.Z), q);
            _probePuppet.Apply(pose);
            _probeWater.Tick(pose, now);
        }
    }
}
