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
    /// Scripted moments (a door slams shut, something howls, a stalker appears) are written for one player: a trigger volume notices
    /// the local player and fires a UnityEvent. In co-op the moment must happen for everyone. This class puts a listener on each such
    /// event of the scene; when the local trigger fires, it reports the event id; every other machine replays the same event on its own
    /// copy of the trigger. A replay never reports itself again (Replaying flag), and an event that fired locally a moment ago is not
    /// replayed on top of itself, so two players walking into the same trigger give one occurrence per machine, not two.
    /// Covered: PlayerTrigger, TriggerEventComponent, MultiTriggerArea, OnPlayerEnter, RandomEventTrigger (their UnityEvents) and the
    /// methods Howler.PlayerEnter, HowlerTrigger.PlayerEnter, Peekaboo.Trigger.
    /// Scripts that move the world around one player (looping corridors, gravity rooms, elevators that need everyone) are not covered.
    /// </summary>
    internal sealed class EventSync
    {
        private const float LocalEchoWindowS = 1.5f;
        private const float RescanEveryS = 3f;

        private sealed class Hook
        {
            public uint Id;
            public string Name;
            public UnityEvent Ev;       // for UnityEvent hooks
            public Action Invoke;       // for method hooks
            public float LastLocalAt = -100f;
        }

        public static bool Replaying;

        private readonly Dictionary<uint, Hook> _hooks = new Dictionary<uint, Hook>();
        private string _scene = "";
        private float _nextScan;
        private int _lastUnityEventFrame = -10;
        private readonly List<KeyValuePair<int, uint>> _pendingMethods = new List<KeyValuePair<int, uint>>();

        public int Hooked => _hooks.Count;
        public int LocalFired, Replayed, SkippedEcho, UnknownId;

        private static EventSync Current => Plugin.Events;

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

        private static uint IdOf(string scene, Component c, string typeName, string eventName) =>
            Fnv(scene + "|" + PathOf(c.transform) + "|" + typeName + "|" + eventName);

        // ---- scene bookkeeping ---------------------------------------------------------------------

        public void Update(CoopSession s, float now)
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != _scene)
            {
                _scene = scene;
                _hooks.Clear();
                _pendingMethods.Clear();
                _nextScan = 0f;
            }
            if (!BodySync.IsGameplayScene(scene) || !s.Connected) return;

            if (now >= _nextScan)
            {
                _nextScan = now + RescanEveryS;
                Scan(scene);
            }

            // Method hooks are reported one frame late, so they can be dropped when a UnityEvent fired in the same frame
            // (the method was then only a consequence of that event, which is replayed as a whole).
            if (_pendingMethods.Count > 0)
            {
                int frame = Time.frameCount;
                for (int i = _pendingMethods.Count - 1; i >= 0; i--)
                {
                    if (_pendingMethods[i].Key >= frame) continue;
                    uint id = _pendingMethods[i].Value;
                    int firedFrame = _pendingMethods[i].Key;
                    _pendingMethods.RemoveAt(i);
                    if (_lastUnityEventFrame >= firedFrame) continue;
                    if (_hooks.TryGetValue(id, out Hook h)) Report(s, h, now);
                }
            }
        }

        private void AddEventHook(string scene, Component owner, string typeName, string eventName, UnityEvent ev)
        {
            if (ev == null) return;
            uint id = IdOf(scene, owner, typeName, eventName);
            if (_hooks.ContainsKey(id)) return;
            var hook = new Hook { Id = id, Name = typeName + "." + eventName, Ev = ev };
            _hooks[id] = hook;
            ev.AddListener((UnityAction)(() => OnUnityEventFired(hook)));
        }

        private void AddMethodHook(string scene, Component owner, string typeName, string methodName, Action invoke)
        {
            uint id = IdOf(scene, owner, typeName, methodName);
            if (_hooks.ContainsKey(id)) return;
            _hooks[id] = new Hook { Id = id, Name = typeName + "." + methodName, Invoke = invoke };
        }

        private void Scan(string scene)
        {
            int before = _hooks.Count;
            const FindObjectsInactive inc = FindObjectsInactive.Include;
            const FindObjectsSortMode none = FindObjectsSortMode.None;
            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsByType<Backrooms.Scripts.Utilities.PlayerTrigger>(inc, none))
                {
                    AddEventHook(scene, c, "PlayerTrigger", "onPlayerEnter", c.onPlayerEnter);
                    AddEventHook(scene, c, "PlayerTrigger", "onPlayerExit", c.onPlayerExit);
                }
                foreach (var c in UnityEngine.Object.FindObjectsByType<Backrooms.Scripts.TriggerEventComponent>(inc, none))
                {
                    AddEventHook(scene, c, "TriggerEventComponent", "OnPlayerEnter", c.OnPlayerEnter);
                    AddEventHook(scene, c, "TriggerEventComponent", "OnPlayerExit", c.OnPlayerExit);
                }
                foreach (var c in UnityEngine.Object.FindObjectsByType<Backrooms.Scripts.MultiTriggerArea>(inc, none))
                {
                    AddEventHook(scene, c, "MultiTriggerArea", "OnPlayerEnter", c.OnPlayerEnter);
                    AddEventHook(scene, c, "MultiTriggerArea", "OnPlayerExit", c.OnPlayerExit);
                }
                foreach (var c in UnityEngine.Object.FindObjectsByType<Backrooms.Scripts.OnPlayerEnter>(inc, none))
                    AddEventHook(scene, c, "OnPlayerEnter", "onEnter", c.onEnter);
                foreach (var c in UnityEngine.Object.FindObjectsByType<Backrooms.Features.RandomEvents.RandomEventTrigger>(inc, none))
                    AddEventHook(scene, c, "RandomEventTrigger", "OnActivated", c.OnActivated);
                foreach (var c in UnityEngine.Object.FindObjectsByType<Howler>(inc, none)) { var h = c; AddMethodHook(scene, h, "Howler", "PlayerEnter", () => h.PlayerEnter()); }
                foreach (var c in UnityEngine.Object.FindObjectsByType<HowlerTrigger>(inc, none)) { var h = c; AddMethodHook(scene, h, "HowlerTrigger", "PlayerEnter", () => h.PlayerEnter()); }
                foreach (var c in UnityEngine.Object.FindObjectsByType<Peekaboo>(inc, none)) { var h = c; AddMethodHook(scene, h, "Peekaboo", "Trigger", () => h.Trigger()); }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"event scan failed: {e.Message}");
            }
            if (_hooks.Count != before) Plugin.Logger.LogInfo($"events: {_hooks.Count - before} new scripted event(s) hooked in '{scene}', {_hooks.Count} in total");
        }

        // ---- local firing --------------------------------------------------------------------------

        private void OnUnityEventFired(Hook h)
        {
            if (Replaying) return;
            _lastUnityEventFrame = Time.frameCount;
            var s = Plugin.Session;
            if (s == null || !s.Connected) return;
            Report(s, h, Time.realtimeSinceStartup);
        }

        /// <summary>Called by the Harmony patches when a hooked method ran locally.</summary>
        public static void MethodFired(Component instance, string typeName, string methodName)
        {
            var self = Current;
            if (self == null || Replaying) return;
            uint id = IdOf(self._scene, instance, typeName, methodName);
            if (!self._hooks.ContainsKey(id)) return;
            self._pendingMethods.Add(new KeyValuePair<int, uint>(Time.frameCount, id));
        }

        private void Report(CoopSession s, Hook h, float now)
        {
            h.LastLocalAt = now;
            LocalFired++;
            s.SendGameEvent(h.Id, 0);
        }

        /// <summary>Fire a hooked UnityEvent as if the local player had triggered it. Test bot only.</summary>
        public List<uint> SortedUnityEventIds()
        {
            var ids = new List<uint>();
            foreach (var kv in _hooks) if (kv.Value.Ev != null && kv.Value.Name.Contains("Enter")) ids.Add(kv.Key);
            ids.Sort();
            return ids;
        }

        public void BotFire(uint id)
        {
            if (_hooks.TryGetValue(id, out Hook h) && h.Ev != null) { h.Ev.Invoke(); }
        }

        // ---- remote firing -------------------------------------------------------------------------

        public void OnRemote(uint id, byte kind, byte from, float now)
        {
            if (!_hooks.TryGetValue(id, out Hook h)) { UnknownId++; return; }
            if (now - h.LastLocalAt < LocalEchoWindowS) { SkippedEcho++; return; }
            Replaying = true;
            try
            {
                if (h.Ev != null) h.Ev.Invoke();
                else h.Invoke?.Invoke();
                Replayed++;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"replaying '{h.Name}' failed: {e.Message}");
            }
            finally { Replaying = false; }
        }
    }

    // Method hooks. The postfix only records that the method ran; EventSync decides whether it is worth reporting.
    [HarmonyPatch(typeof(Howler), nameof(Howler.PlayerEnter))]
    internal static class HowlerPlayerEnterHook { private static void Postfix(Howler __instance) => EventSync.MethodFired(__instance, "Howler", "PlayerEnter"); }

    [HarmonyPatch(typeof(HowlerTrigger), nameof(HowlerTrigger.PlayerEnter))]
    internal static class HowlerTriggerPlayerEnterHook { private static void Postfix(HowlerTrigger __instance) => EventSync.MethodFired(__instance, "HowlerTrigger", "PlayerEnter"); }

    [HarmonyPatch(typeof(Peekaboo), nameof(Peekaboo.Trigger))]
    internal static class PeekabooTriggerHook { private static void Postfix(Peekaboo __instance) => EventSync.MethodFired(__instance, "Peekaboo", "Trigger"); }
}
