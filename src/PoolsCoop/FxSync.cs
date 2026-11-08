using System;
using HarmonyLib;
using PoolsCoop.Net;
using UnityEngine;

namespace PoolsCoop
{
    /// <summary>
    /// Footsteps and water splashes of the local player, repeated at the friend's puppet on every other machine.
    /// The game already decides when the local player makes a step (surface material, depth of the water under the feet) and when the
    /// body or a hand breaks the water surface; small Harmony prefixes on those methods report each one as an effect message, and the
    /// receiver plays the same game code at that place: PlayFootstepSound for the step (with a ripple and spray in water), the game's
    /// splash sounds plus ripple and spray for a splash. A replay never reports itself (Replaying flag).
    /// </summary>
    internal static class FxSync
    {
        public static bool Replaying;
        public static int SentSteps, SentSplashes, ReplayedSteps, ReplayedSplashes, Failures;
        private static float _lastStepAt = -10f;

        private static bool Live
        {
            get
            {
                var s = Plugin.Session;
                return s != null && s.Connected && !Replaying;
            }
        }

        private static Vec3 V(Vector3 v) => new Vec3(v.x, v.y, v.z);

        /// <summary>A hand's splash spawner (the VR build has a field for this, the flat build does not; the component is in both).</summary>
        internal static bool IsHandSpawner(Backrooms.Features.WaterDeform.RigidbodyRippleSpawner sp)
        {
            try { return sp.GetComponent<Backrooms.Features.VR.VRHandWaterDetector>() != null || sp.GetComponent<Autohand.Hand>() != null; }
            catch (Exception) { return false; }
        }

        /// <summary>The local player's body (flat FPSController or the VR "XR Player Body"; the VR one has isPlayer = false).</summary>
        internal static bool IsBodySpawner(Backrooms.Features.WaterDeform.RigidbodyRippleSpawner sp)
        {
            try { return sp.GetComponent<Backrooms.Features.Player.PlayerBody>() != null || sp.GetComponent<FPSController>() != null; }
            catch (Exception) { return false; }
        }

        // ---- local side -----------------------------------------------------------------------------

        internal static void LocalFootstep(Vector3 ground, float volume, int direction, int surface, float depth)
        {
            if (!Live) return;
            float now = Time.realtimeSinceStartup;
            if (now - _lastStepAt < 0.12f) return; // the game can call twice for one step (both feet / stairs); one is enough
            _lastStepAt = now;
            Plugin.Session.SendFx(new FxEvent { Kind = FxKind.Footstep, Pos = V(ground), A = volume, B = depth, C = (byte)Mathf.Clamp(surface, 0, 255), D = (byte)(direction & 0xFF) });
            SentSteps++;
        }

        internal static void LocalSplash(Backrooms.Features.WaterDeform.RigidbodyRippleSpawner sp, bool enter)
        {
            if (!Live || sp == null) return;
            bool hand = FxSync.IsHandSpawner(sp);
            bool body = !hand && IsBodySpawner(sp);
            if (!hand && !body) return; // ducks and other floating things are shared as bodies, each machine splashes them itself
            float speed = 1f;
            try { if (sp.rb != null) speed = sp.rb.linearVelocity.magnitude; } catch (Exception) { }
            Plugin.Session.SendFx(new FxEvent { Kind = hand ? FxKind.HandSplash : FxKind.BodySplash, Pos = V(sp.transform.position), A = speed, B = enter ? 1f : 0f });
            SentSplashes++;
        }

        internal static void LocalBigSplash(Vector3 pos, int type, float airTime)
        {
            if (!Live) return;
            Plugin.Session.SendFx(new FxEvent { Kind = FxKind.BigSplash, Pos = V(pos), A = airTime, C = (byte)Mathf.Clamp(type, 0, 255) });
            SentSplashes++;
        }

        // ---- bot (test only) ------------------------------------------------------------------------

        private static float _botNext = -1f;
        private static int _botCount;
        public static int BotCalls, BotErrors;

        /// <summary>
        /// Stand-in for a walking player: calls the game's own footstep method (dry floor, then in the water of the first pool) and
        /// the game's own splash method of the local body, so the Harmony prefixes send effects and the other machine replays them.
        /// </summary>
        public static void RunBot(Net.CoopSession s, float now)
        {
            if (!Plugin.FxBot || s == null || !s.Connected) return;
            if (!BodySync.IsGameplayScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)) return;
            if (_botNext < 0f) { _botNext = now + 8f; Plugin.Logger.LogInfo("fx bot: armed, starts in 8 s"); return; }
            if (now < _botNext || _botCount >= 40) return;
            _botNext = now + 0.45f;
            _botCount++;
            try
            {
                Vector3 water = Vector3.zero;
                bool haveWater = false;
                foreach (var w in UnityEngine.Object.FindObjectsByType<WaterBodyVolume>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    var cols = w.GetComponentsInChildren<Collider>(false);
                    if (cols.Length == 0) continue;
                    Vector3 c = cols[0].bounds.center;
                    Unity.Mathematics.float3 sf = w.ProjectToWaterSurface(new Unity.Mathematics.float3(c.x, c.y, c.z));
                    water = new Vector3(sf.x, sf.y - 0.3f, sf.z);
                    haveWater = true;
                    break;
                }
                if (_botCount == 1)
                {
                    int spawners = 0, players = 0;
                    foreach (var sp in UnityEngine.Object.FindObjectsByType<Backrooms.Features.WaterDeform.RigidbodyRippleSpawner>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    { spawners++; if (sp.isPlayer) players++; }
                    Plugin.Logger.LogInfo($"fx bot: water={haveWater} footstepManager={Backrooms.Scripts.PlayerFootstepsAudioManager.IsInstanced} activeRippleSpawners={spawners} playerSpawners={players}");
                }
                if (_botCount % 10 == 0 && haveWater)
                {
                    LocalBigSplash(water + Vector3.up * 0.3f, 1, 0.6f); // as if the player fell into the pool
                    BotCalls++;
                    return;
                }
                if (_botCount % 10 == 5 && haveWater)
                {
                    // the local body's splash spawner, as when the player jumps in
                    foreach (var sp in UnityEngine.Object.FindObjectsByType<Backrooms.Features.WaterDeform.RigidbodyRippleSpawner>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    {
                        if (sp == null || !IsBodySpawner(sp)) continue;
                        sp.Splash(true);
                        BotCalls++;
                        break;
                    }
                    return;
                }
                if (!Backrooms.Scripts.PlayerFootstepsAudioManager.IsInstanced) return;
                var m = Backrooms.Scripts.PlayerFootstepsAudioManager.Instance;
                bool wet = haveWater && _botCount % 2 == 0;
                Vector3 at = wet ? water : (FPSController.IsInstanced ? FPSController.Instance.transform.position : Vector3.zero);
                m.PlayFootstepSound(at, 0.8f, _botCount % 2 == 0 ? 1 : -1, wet ? Backrooms.Scripts.MaterialTypes.Water : Backrooms.Scripts.MaterialTypes.Tile, wet ? 0.3f : 0f);
                BotCalls++;
            }
            catch (Exception e)
            {
                BotErrors++;
                if (BotErrors < 3) Plugin.Logger.LogWarning($"fx bot: {e.Message}");
            }
        }

        // ---- remote side ----------------------------------------------------------------------------

        public static void OnRemote(FxEvent e)
        {
            var pos = new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z);
            Replaying = true;
            try
            {
                switch (e.Kind)
                {
                    case FxKind.Footstep:
                        Step(pos, e.A, e.D, e.C, e.B);
                        ReplayedSteps++;
                        break;
                    case FxKind.BodySplash:
                        PuppetWater.SplashAt(pos, Mathf.Clamp(e.A, 0.3f, 8f), e.B > 0.5f ? PuppetWater.SoundBodyEnter : PuppetWater.SoundBodyExit);
                        ReplayedSplashes++;
                        break;
                    case FxKind.HandSplash:
                        PuppetWater.SplashAt(pos, Mathf.Clamp(e.A, 0.3f, 6f), e.B > 0.5f ? PuppetWater.SoundHandEnter : PuppetWater.SoundHandExit);
                        ReplayedSplashes++;
                        break;
                    case FxKind.BigSplash:
                        PuppetWater.SplashAt(pos, Mathf.Clamp(2f + e.A * 3f, 2f, 8f), e.C == 2 ? PuppetWater.SoundHandEnter : PuppetWater.SoundBodyEnter);
                        ReplayedSplashes++;
                        break;
                }
            }
            catch (Exception ex)
            {
                Failures++;
                if (Failures < 5) Plugin.Logger.LogWarning($"fx replay failed: {ex.Message}");
            }
            finally { Replaying = false; }
        }

        private static void Step(Vector3 ground, float volume, byte direction, byte surface, float depth)
        {
            if (Backrooms.Scripts.PlayerFootstepsAudioManager.IsInstanced)
            {
                var m = Backrooms.Scripts.PlayerFootstepsAudioManager.Instance;
                // PlayFootstepSound is written for the local player and may update the local wetness; keep ours as it was.
                bool inWater = m.inWater;
                float wet = m.wetness;
                try { m.PlayFootstepSound(ground, Mathf.Clamp(volume, 0f, 2f), (int)(sbyte)direction,(Backrooms.Scripts.MaterialTypes)surface, Mathf.Max(0f, depth)); }
                finally { m.inWater = inWater; m.wetness = wet; }
            }
            if (depth > 0.03f) PuppetWater.SplashAt(ground + Vector3.up * Mathf.Min(depth, 1f), 0.8f + depth, PuppetWater.SoundNone);
        }
    }

    [HarmonyPatch(typeof(Backrooms.Scripts.PlayerFootstepsAudioManager), nameof(Backrooms.Scripts.PlayerFootstepsAudioManager.PlayFootstepSound))]
    internal static class FootstepPatch
    {
        private static void Prefix(Vector3 groundPoint, float volume, int direction, Backrooms.Scripts.MaterialTypes surfaceType, float waterDepth)
        {
            try { FxSync.LocalFootstep(groundPoint, volume, direction, (int)surfaceType, waterDepth); }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(Backrooms.Features.WaterDeform.RigidbodyRippleSpawner), nameof(Backrooms.Features.WaterDeform.RigidbodyRippleSpawner.Splash))]
    internal static class RippleSplashPatch
    {
        private static void Prefix(Backrooms.Features.WaterDeform.RigidbodyRippleSpawner __instance, bool enter)
        {
            try { FxSync.LocalSplash(__instance, enter); }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(SplashSpawner), nameof(SplashSpawner.PlaySplashDelayed))]
    internal static class BigSplashPatch
    {
        private static void Prefix(SplashSpawner __instance, Il2CppSystem.Nullable<Vector3> pos, SplashType splashType, float airTime)
        {
            try
            {
                Vector3 p = pos != null && pos.HasValue ? pos.Value : __instance.transform.position;
                FxSync.LocalBigSplash(p, (int)splashType, airTime);
            }
            catch (Exception) { }
        }
    }
}
