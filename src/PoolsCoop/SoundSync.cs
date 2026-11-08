using System;
using System.Collections.Generic;
using HarmonyLib;
using PoolsCoop.Net;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace PoolsCoop
{
    /// <summary>
    /// Random and scary sounds heard together. The game plays them through FMOD one-shots from a few kinds of objects:
    /// <list type="bullet">
    /// <item><c>ScatteredOneShotAudioPlayer</c>: a random sound from a list at a random place on a random timer. Each machine would roll
    /// its own, so in co-op only the host runs them and every sound it plays is sent to the others (same sound, same place).</item>
    /// <item><c>OffScreenDetectorAudioPlayer</c>: a sound behind you when you look away; <c>PlayOneSoundTrigger</c>: a sound when you walk
    /// into a trigger. When either plays for one player, it is sent and plays for everyone (a trigger that already played is marked as
    /// played on the others, so it does not play a second time when they walk in).</item>
    /// <item><c>OnStartRandomEvent</c>: a chance roll when a chapter starts (does a scare happen this time?). In co-op the roll comes from
    /// the session seed and the object, so every machine gets the same answer.</item>
    /// </list>
    /// How it knows a sound played: the host runs its own copy of the random players and sends what it picks; for the other two the
    /// plugin compares the object's own counters before and after (spawns, _played). FMOD itself is not patched (see below).
    /// </summary>
    internal static class SoundSync
    {
        public const byte KindScattered = 1, KindOffscreen = 2, KindTrigger = 3, KindBot = 9;

        public static bool Replaying;
        public static int Captured, Played, SkippedClient, UnknownSource, StartRolls, Failures;


        // source id -> object, per scene (for "already played" marking and to drop sounds from another scene)
        private static readonly Dictionary<uint, Component> Sources = new Dictionary<uint, Component>();
        private static string _sourcesScene = "";
        private static float _nextSourceScan;

        private static CoopSession Live
        {
            get
            {
                var s = Plugin.Session;
                return s != null && s.Connected ? s : null;
            }
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name + "#" + x.GetSiblingIndex());
            parts.Reverse();
            return string.Join("/", parts);
        }

        internal static uint IdOf(Component c) => SharedRandom.Hash(SceneManager.GetActiveScene().name + "|" + PathOf(c.transform) + "|" + c.GetIl2CppType().Name);

        /// <summary>True when a client should leave the host's random sound players to the host (both in the same chapter).</summary>
        internal static bool ClientDefersToHost()
        {
            var s = Live;
            return s != null && s.Role == SessionRole.Client && !Replaying && s.HostScene == SceneManager.GetActiveScene().name;
        }

        /// <summary>True on the host of a co-op session: it runs the random sound players for everyone.</summary>
        internal static bool HostRunsForAll()
        {
            var s = Live;
            return s != null && s.Role == SessionRole.Host && !Replaying;
        }

        /// <summary>Sends one game sound (an FMOD event reference) at a place to the other players.</summary>
        internal static void Send(Component src, FMODUnity.EventReference ev, Vector3 pos, byte kind)
        {
            var s = Live;
            if (s == null || Replaying || src == null || ev.IsNull) return;
            try
            {
                var g = ev.Guid;
                s.SendSound(new SoundEvent { G1 = (uint)g.Data1, G2 = (uint)g.Data2, G3 = (uint)g.Data3, G4 = (uint)g.Data4, Pos = new Vec3(pos.x, pos.y, pos.z), Source = IdOf(src), Flags = kind });
                Captured++;
            }
            catch (Exception e) { Failures++; if (Failures < 4) Plugin.Logger.LogWarning($"sound send failed: {e.Message}"); }
        }

        /// <summary>
        /// The host's copy of ScatteredOneShotAudioPlayer.Update, driven by that object's own settings (wait time range, sound list,
        /// play positions, random offset, play limit): when it picks a sound, it plays it here and sends the same sound and place.
        /// </summary>
        internal static void RunScattered(Backrooms.Scripts.ScatteredOneShotAudioPlayer p)
        {
            p.waitTime -= Time.deltaTime;
            if (p.waitTime > 0f) return;
            Vector2 range = p.waitTimeRange;
            p.waitTime = UnityEngine.Random.Range(Mathf.Min(range.x, range.y), Mathf.Max(range.x, range.y));
            if (p.usePlayLimit)
            {
                if (p.numLimitedPlaysRemaining <= 0) return;
                p.numLimitedPlaysRemaining--;
            }
            var evs = p.audioEvents;
            if (evs == null || evs.Count == 0) return;
            FMODUnity.EventReference ev = evs[UnityEngine.Random.Range(0, evs.Count)];
            if (ev.IsNull) return;
            Transform at = p.transform;
            if (p.useChildrenAsPlayPositions && p.transform.childCount > 0) at = p.transform.GetChild(UnityEngine.Random.Range(0, p.transform.childCount));
            else if (p.playPositionTransforms != null && p.playPositionTransforms.Count > 0)
            {
                var t = p.playPositionTransforms[UnityEngine.Random.Range(0, p.playPositionTransforms.Count)];
                if (t != null) at = t;
            }
            Vector3 o = p.positionRandomOffsetRange;
            Vector3 pos = at.position + new Vector3(UnityEngine.Random.Range(-o.x, o.x), UnityEngine.Random.Range(-o.y, o.y), UnityEngine.Random.Range(-o.z, o.z));
            Replaying = true;
            try { FMODUnity.RuntimeManager.PlayOneShot(ev, pos); }
            finally { Replaying = false; }
            HostScatteredPlays++;
            Send(p, ev, pos, KindScattered);
        }

        public static int HostScatteredPlays;

        private static void ScanSources(bool force)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (!force && scene == _sourcesScene) return;
            if (force && Time.realtimeSinceStartup < _nextSourceScan && scene == _sourcesScene) return;
            _sourcesScene = scene;
            _nextSourceScan = Time.realtimeSinceStartup + 2f;
            Sources.Clear();
            void Add(Component c) { if (c != null) Sources[IdOf(c)] = c; }
            foreach (var c in UnityEngine.Object.FindObjectsByType<Backrooms.Scripts.ScatteredOneShotAudioPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Add(c);
            foreach (var c in UnityEngine.Object.FindObjectsByType<Backrooms.Scripts.OffScreenDetectorAudioPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Add(c);
            foreach (var c in UnityEngine.Object.FindObjectsByType<PlayOneSoundTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None)) Add(c);
        }

        /// <summary>A sound another player's game played: play the same FMOD event at the same place.</summary>
        public static void OnRemote(SoundEvent e)
        {
            try
            {
                Component src = null;
                if (e.Flags != KindBot)
                {
                    ScanSources(false);
                    if (!Sources.TryGetValue(e.Source, out src) || src == null)
                    {
                        ScanSources(true);
                        if (!Sources.TryGetValue(e.Source, out src) || src == null) { UnknownSource++; return; } // not in our scene
                    }
                }
                if (e.Flags == KindTrigger)
                {
                    var trig = src.TryCast<PlayOneSoundTrigger>();
                    if (trig != null) trig._played = true; // it has played for everyone now
                }
                var g = new FMOD.GUID { Data1 = (int)e.G1, Data2 = (int)e.G2, Data3 = (int)e.G3, Data4 = (int)e.G4 };
                Replaying = true;
                try { FMODUnity.RuntimeManager.PlayOneShot(g, new Vector3(e.Pos.X, e.Pos.Y, e.Pos.Z)); }
                finally { Replaying = false; }
                Played++;
            }
            catch (Exception ex) { Failures++; if (Failures < 4) Plugin.Logger.LogWarning($"sound replay failed: {ex.Message}"); }
        }

        /// <summary>The same chance roll on every machine of the session (false when not in a session: the game rolls itself).</summary>
        internal static bool TrySharedRoll(Component c, float probability, out bool success)
        {
            success = false;
            var s = Live;
            if (s == null || s.Seed == 0) return false;
            double u = SharedRandom.Unit(s.Seed, IdOf(c));
            success = u < probability;
            StartRolls++;
            return true;
        }

        // ---- bot (test only) ------------------------------------------------------------------------

        private static float _botAt = -1f;
        private static bool _botDone;
        public static int BotPlayers, BotScattered, BotStartRandom, BotTriggers, BotOffscreen;

        /// <summary>
        /// Counts the sound objects of the chapter and makes the host's random sound players fire at once (wait time 0), so the test
        /// shows whether their sounds go through the captured FMOD calls and reach the client.
        /// </summary>
        public static void RunBot(CoopSession s, float now)
        {
            if (!Plugin.FxBot || s == null || !s.Connected || _botDone) return;
            if (!BodySync.IsGameplayScene(SceneManager.GetActiveScene().name)) return;
            if (_botAt < 0f) { _botAt = now + 10f; return; }
            if (now < _botAt) return;
            _botDone = true;
            try
            {
                var scattered = UnityEngine.Object.FindObjectsByType<Backrooms.Scripts.ScatteredOneShotAudioPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                BotScattered = scattered.Length;
                BotStartRandom = UnityEngine.Object.FindObjectsByType<Backrooms.Scripts.OnStartRandomEvent>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                BotTriggers = UnityEngine.Object.FindObjectsByType<PlayOneSoundTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                BotOffscreen = UnityEngine.Object.FindObjectsByType<Backrooms.Scripts.OffScreenDetectorAudioPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                if (s.Role == SessionRole.Host)
                    foreach (var p in scattered)
                    {
                        Plugin.Logger.LogInfo($"sound bot: scattered player enabled={p.enabled} active={p.isActiveAndEnabled} events={(p.audioEvents != null ? p.audioEvents.Count : -1)} waitTime={p.waitTime:F1} limit={p.usePlayLimit}/{p.numLimitedPlaysRemaining}");
                        p.waitTime = 0f;
                        if (!p.isActiveAndEnabled) RunScattered(p); // Unity does not update a disabled one: run our copy once by hand
                        BotPlayers++;
                    }
                // and one sound sent directly, so the path works even in a chapter with no random sound player
                var fs = Backrooms.Scripts.PlayerFootstepsAudioManager.IsInstanced ? Backrooms.Scripts.PlayerFootstepsAudioManager.Instance.soakedAudio : default(FMODUnity.EventReference);
                if (!fs.IsNull)
                {
                    var g = fs.Guid;
                    s.SendSound(new SoundEvent { G1 = (uint)g.Data1, G2 = (uint)g.Data2, G3 = (uint)g.Data3, G4 = (uint)g.Data4, Pos = new Vec3(0, 0, 0), Flags = KindBot });
                }
                Plugin.Logger.LogInfo($"sound bot: scatteredPlayers={BotScattered} onStartRandom={BotStartRandom} playOneSoundTriggers={BotTriggers} offscreenPlayers={BotOffscreen} forced={BotPlayers} seed={(s.Seed != 0)}");
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"sound bot: {e.Message}"); }
        }
    }
    // ---- sources --------------------------------------------------------------------------------------
    // (FMOD's RuntimeManager.PlayOneShot cannot be patched: it returns a Nullable struct and the IL2CPP trampoline crashes on it,
    // so the plugin watches the sound objects themselves.)

    [HarmonyPatch(typeof(Backrooms.Scripts.ScatteredOneShotAudioPlayer), nameof(Backrooms.Scripts.ScatteredOneShotAudioPlayer.Update))]
    internal static class ScatteredPatch
    {
        private static bool Prefix(Backrooms.Scripts.ScatteredOneShotAudioPlayer __instance)
        {
            try
            {
                if (SoundSync.ClientDefersToHost()) { SoundSync.SkippedClient++; return false; } // the host rolls these for everyone
                if (SoundSync.HostRunsForAll()) { SoundSync.RunScattered(__instance); return false; }
            }
            catch (Exception e) { SoundSync.Failures++; if (SoundSync.Failures < 4) Plugin.Logger.LogWarning($"scattered sound failed: {e.Message}"); }
            return true; // not in co-op (or our copy failed): the game's own code
        }
    }

    [HarmonyPatch(typeof(Backrooms.Scripts.OffScreenDetectorAudioPlayer), nameof(Backrooms.Scripts.OffScreenDetectorAudioPlayer.OnOffscreenEvent))]
    internal static class OffscreenPatch
    {
        private static void Prefix(Backrooms.Scripts.OffScreenDetectorAudioPlayer __instance, out int __state)
        {
            __state = -1;
            try { __state = __instance.spawns; } catch (Exception) { }
        }

        private static void Postfix(Backrooms.Scripts.OffScreenDetectorAudioPlayer __instance, Backrooms.Scripts.OffScreenDetectorArea area, int __state)
        {
            try
            {
                if (__state < 0 || __instance.spawns <= __state) return; // the chance roll said no
                Vector3 pos = area != null ? area.transform.position : __instance.transform.position;
                SoundSync.Send(__instance, __instance.audioEventRef, pos, SoundSync.KindOffscreen);
            }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(PlayOneSoundTrigger), nameof(PlayOneSoundTrigger.OnTriggerEnter))]
    internal static class OneSoundTriggerPatch
    {
        private static void Prefix(PlayOneSoundTrigger __instance, out bool __state)
        {
            __state = true;
            try { __state = __instance._played; } catch (Exception) { }
        }

        private static void Postfix(PlayOneSoundTrigger __instance, bool __state)
        {
            try
            {
                if (__state || !__instance._played) return; // did not play now
                Vector3 pos = __instance.playAtChildPosition && __instance._child != null ? __instance._child.position : __instance.transform.position;
                SoundSync.Send(__instance, __instance.audioEventRef, pos, SoundSync.KindTrigger);
            }
            catch (Exception) { }
        }
    }

    [HarmonyPatch(typeof(Backrooms.Scripts.OnStartRandomEvent), nameof(Backrooms.Scripts.OnStartRandomEvent.Start))]
    internal static class StartRandomPatch
    {
        private static bool Prefix(Backrooms.Scripts.OnStartRandomEvent __instance)
        {
            try
            {
                if (!SoundSync.TrySharedRoll(__instance, __instance.successProbability, out bool ok)) return true; // not in co-op: the game rolls
                UnityEvent ev = ok ? __instance.OnSuccess : __instance.OnFail;
                ev?.Invoke();
                return false;
            }
            catch (Exception) { return true; }
        }
    }
}
