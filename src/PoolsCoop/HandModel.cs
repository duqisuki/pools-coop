using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PoolsCoop.Net;
using UnityEngine;

namespace PoolsCoop
{
    /// <summary>
    /// The game's own VR hand (the skinned "pools-vr-hands" meshes, male and female, left and right) captured once from the player rig.
    /// Both builds carry the rig in every chapter (inactive in the flat build), so a template can always be read. A template is plain
    /// data (bone names, rest pose, the open and closed rotation of every finger joint, mesh and materials): building a puppet hand from
    /// it never touches the local player's hand objects, and the copy has no physics, colliders or scripts.
    /// </summary>
    internal sealed class HandTemplate
    {
        public string Name;
        public bool Left;
        public bool Female;
        public Mesh Mesh;
        public Material[] Mats;
        public Vector3 RootScale;

        // Every transform under the hand root, parent before child. Index 0 is the root itself.
        public readonly List<string> Names = new List<string>();
        public readonly List<int> Parents = new List<int>();
        public readonly List<Vector3> LPos = new List<Vector3>();
        public readonly List<Quaternion> LRot = new List<Quaternion>();
        public readonly List<Vector3> LScale = new List<Vector3>();
        public int RendererNode = -1;
        public int RootBone = -1;
        public int[] Bones;

        // Per finger (0 thumb, 1 index, 2 middle, 3 ring, 4 pinky): node indices of the joints and their open/closed local rotations.
        public readonly int[][] FingerJoints = new int[5][];
        public readonly Quaternion[][] Open = new Quaternion[5][];
        public readonly Quaternion[][] Closed = new Quaternion[5][];
        public bool PosesFromGame;

        // Hand frame in the root's space (after the root's own scale): which way the fingers point, which way the palm faces.
        public Vector3 FingerDir = Vector3.up;
        public Vector3 PalmDir = Vector3.left;
        public Vector3 PalmCenter = Vector3.zero;

        public string SkinProperty = "";
        public bool Valid => Mesh != null && Bones != null;

        private static readonly Dictionary<string, HandTemplate> Cache = new Dictionary<string, HandTemplate>();
        private static float _nextSearch;
        public static int Captured, Failed;

        private static string Key(bool left, bool female) => (left ? "L" : "R") + (female ? "F" : "M");

        /// <summary>The template for one hand; captured from the scene the first time (searches at most once a second when missing).</summary>
        public static HandTemplate Get(bool left, bool female)
        {
            if (Cache.TryGetValue(Key(left, female), out HandTemplate t) && t.Valid) return t;
            if (Cache.TryGetValue(Key(left, !female), out HandTemplate other) && other.Valid && Time.realtimeSinceStartup < _nextSearch) return other;
            if (Time.realtimeSinceStartup < _nextSearch) return null;
            _nextSearch = Time.realtimeSinceStartup + 1f;
            try { CaptureAll(); }
            catch (Exception e) { Failed++; Plugin.Logger.LogWarning($"hands: capture failed: {e.Message}"); }
            if (Cache.TryGetValue(Key(left, female), out t) && t.Valid) return t;
            if (Cache.TryGetValue(Key(left, !female), out t) && t.Valid) return t;
            return null;
        }

        private static void CaptureAll()
        {
            string skinProp = "";
            try
            {
                foreach (var m in UnityEngine.Object.FindObjectsByType<Backrooms.Features.VR.VRHandsMaterialManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (m != null && !string.IsNullOrEmpty(m.skinTonePropertyName)) { skinProp = m.skinTonePropertyName; break; }
            }
            catch (Exception) { }

            foreach (var h in UnityEngine.Object.FindObjectsByType<Autohand.Hand>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (h == null) continue;
                string n = h.gameObject.name;
                if (!n.Contains("AutoHand")) continue; // only the player's hand prefabs
                bool female = n.IndexOf("Female", StringComparison.OrdinalIgnoreCase) >= 0;
                bool left = h.left;
                string key = Key(left, female);
                if (Cache.TryGetValue(key, out HandTemplate old) && old.Valid) continue;
                var t = Capture(h, left, female);
                if (t == null) continue;
                t.SkinProperty = skinProp;
                Cache[key] = t;
                Captured++;
                Plugin.Logger.LogInfo(FormattableString.Invariant(
                    $"hands: captured '{n}' left={left} female={female} nodes={t.Names.Count} bones={t.Bones.Length} fingerPosesFromGame={t.PosesFromGame} fingerDir=({t.FingerDir.x:F2},{t.FingerDir.y:F2},{t.FingerDir.z:F2}) palmDir=({t.PalmDir.x:F2},{t.PalmDir.y:F2},{t.PalmDir.z:F2}) skinProp='{skinProp}'"));
            }
        }

        private static int FingerSlot(string jointName)
        {
            string n = jointName.ToLowerInvariant();
            if (n.Contains("thumb")) return 0;
            if (n.Contains("index")) return 1;
            if (n.Contains("middle")) return 2;
            if (n.Contains("ring")) return 3;
            if (n.Contains("pinky") || n.Contains("little")) return 4;
            return -1;
        }

        private static HandTemplate Capture(Autohand.Hand h, bool left, bool female)
        {
            Transform root = h.transform;
            SkinnedMeshRenderer smr = null;
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r != null && r.sharedMesh != null) { smr = r; break; }
            if (smr == null) return null;

            var t = new HandTemplate { Name = h.gameObject.name, Left = left, Female = female, Mesh = smr.sharedMesh, RootScale = root.localScale };
            var mats = smr.sharedMaterials;
            t.Mats = new Material[mats.Length];
            for (int i = 0; i < mats.Length; i++) t.Mats[i] = mats[i];

            // Flatten the hierarchy (parent first).
            var index = new Dictionary<IntPtr, int>();
            var queue = new List<Transform> { root };
            for (int q = 0; q < queue.Count; q++)
            {
                Transform x = queue[q];
                index[x.Pointer] = q;
                t.Names.Add(x.name);
                t.Parents.Add(q == 0 ? -1 : index[x.parent.Pointer]);
                t.LPos.Add(q == 0 ? Vector3.zero : x.localPosition);
                t.LRot.Add(q == 0 ? Quaternion.identity : x.localRotation);
                t.LScale.Add(q == 0 ? Vector3.one : x.localScale);
                for (int c = 0; c < x.childCount; c++) queue.Add(x.GetChild(c));
            }
            if (!index.TryGetValue(smr.transform.Pointer, out t.RendererNode)) return null;
            if (smr.rootBone != null) index.TryGetValue(smr.rootBone.Pointer, out t.RootBone);
            var bones = smr.bones;
            t.Bones = new int[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null || !index.TryGetValue(bones[i].Pointer, out int bi)) return null;
                t.Bones[i] = bi;
            }

            // Finger joints by name (f_index.01.L, thumb.02.L ...), in hierarchy order so .01 comes before .02.
            var lists = new List<int>[5];
            for (int f = 0; f < 5; f++) lists[f] = new List<int>();
            for (int i = 1; i < t.Names.Count; i++)
            {
                int slot = FingerSlot(t.Names[i]);
                if (slot >= 0 && t.Names[i].Contains(".0")) lists[slot].Add(i);
            }
            for (int f = 0; f < 5; f++)
            {
                t.FingerJoints[f] = lists[f].ToArray();
                t.Open[f] = new Quaternion[t.FingerJoints[f].Length];
                t.Closed[f] = new Quaternion[t.FingerJoints[f].Length];
                for (int j = 0; j < t.FingerJoints[f].Length; j++)
                {
                    t.Open[f][j] = t.LRot[t.FingerJoints[f][j]];
                    t.Closed[f][j] = t.LRot[t.FingerJoints[f][j]];
                }
            }

            // Open and closed poses as the game's Autohand fingers define them: bend the rig's finger to 0 and to 1, read the joints,
            // put the joints back exactly as they were (same frame, so nothing is ever drawn bent).
            int fromGame = 0;
            try
            {
                foreach (var finger in h.fingers)
                {
                    if (finger == null) continue;
                    var joints = finger.FingerJoints;
                    if (joints == null || joints.Length == 0) continue;
                    int slot = FingerSlot(joints[0].name);
                    if (slot < 0 || t.FingerJoints[slot].Length == 0) continue;
                    var saved = new Quaternion[joints.Length];
                    var savedPos = new Vector3[joints.Length];
                    for (int j = 0; j < joints.Length; j++) { saved[j] = joints[j].localRotation; savedPos[j] = joints[j].localPosition; }
                    try
                    {
                        finger.SetFingerBend(0f, Autohand.FingerPoseEnum.Open, Autohand.FingerPoseEnum.Closed);
                        var open = new Dictionary<string, Quaternion>();
                        for (int j = 0; j < joints.Length; j++) open[joints[j].name] = joints[j].localRotation;
                        finger.SetFingerBend(1f, Autohand.FingerPoseEnum.Open, Autohand.FingerPoseEnum.Closed);
                        bool changed = false;
                        for (int k = 0; k < t.FingerJoints[slot].Length; k++)
                        {
                            string jn = t.Names[t.FingerJoints[slot][k]];
                            for (int j = 0; j < joints.Length; j++)
                            {
                                if (joints[j].name != jn) continue;
                                t.Open[slot][k] = open[jn];
                                t.Closed[slot][k] = joints[j].localRotation;
                                if (Quaternion.Angle(open[jn], joints[j].localRotation) > 5f) changed = true;
                            }
                        }
                        if (changed) fromGame++;
                    }
                    finally
                    {
                        for (int j = 0; j < joints.Length; j++) { joints[j].localRotation = saved[j]; joints[j].localPosition = savedPos[j]; }
                    }
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning($"hands: reading finger poses failed: {e.Message}"); }
            t.PosesFromGame = fromGame >= 4;
            if (!t.PosesFromGame) MakeSyntheticCurl(t);

            ComputeFrame(t);
            return t;
        }

        /// <summary>Fallback when the game's finger poses could not be read: curl every joint about the axis across the hand.</summary>
        private static void MakeSyntheticCurl(HandTemplate t)
        {
            Vector3[] open = WorldishPositions(t, false);
            Vector3 across = Vector3.zero;
            if (t.FingerJoints[1].Length > 0 && t.FingerJoints[4].Length > 0)
                across = (open[t.FingerJoints[1][0]] - open[t.FingerJoints[4][0]]).normalized;
            for (int f = 0; f < 5; f++)
            {
                for (int j = 0; j < t.FingerJoints[f].Length; j++)
                {
                    int node = t.FingerJoints[f][j];
                    Quaternion parentRot = RootRotationOf(t, t.Parents[node]);
                    Vector3 axisLocal = Quaternion.Inverse(parentRot * t.LRot[node]) * (across.sqrMagnitude > 0 ? across : Vector3.forward);
                    float deg = f == 0 ? 35f : 75f;
                    t.Open[f][j] = t.LRot[node];
                    t.Closed[f][j] = t.LRot[node] * Quaternion.AngleAxis(deg, axisLocal);
                }
            }
        }

        private static Quaternion RootRotationOf(HandTemplate t, int node)
        {
            Quaternion q = Quaternion.identity;
            for (int i = node; i > 0; i = t.Parents[i]) q = t.LRot[i] * q;
            return q;
        }

        /// <summary>Positions of every node in the root's space, open or closed pose (no root scale).</summary>
        private static Vector3[] WorldishPositions(HandTemplate t, bool closed)
        {
            int n = t.Names.Count;
            var rot = new Quaternion[n];
            var pos = new Vector3[n];
            var lrot = new Quaternion[n];
            for (int i = 0; i < n; i++) lrot[i] = t.LRot[i];
            if (closed)
                for (int f = 0; f < 5; f++)
                    for (int j = 0; j < t.FingerJoints[f].Length; j++) lrot[t.FingerJoints[f][j]] = t.Closed[f][j];
            rot[0] = Quaternion.identity;
            pos[0] = Vector3.zero;
            for (int i = 1; i < n; i++)
            {
                int p = t.Parents[i];
                Vector3 s = t.LScale[p];
                pos[i] = pos[p] + rot[p] * Vector3.Scale(s, t.LPos[i]);
                rot[i] = rot[p] * lrot[i];
            }
            return pos;
        }

        /// <summary>Works out which way the fingers point and which way the palm faces, from the bones (no guessing of mesh axes).</summary>
        private static void ComputeFrame(HandTemplate t)
        {
            Vector3[] open = WorldishPositions(t, false);
            Vector3[] closed = WorldishPositions(t, true);
            int mid = t.FingerJoints[2].Length > 0 ? t.FingerJoints[2][t.FingerJoints[2].Length - 1] : -1;
            int midBase = t.FingerJoints[2].Length > 0 ? t.FingerJoints[2][0] : -1;
            if (mid < 0) return;
            Vector3 wrist = t.RootBone > 0 ? open[t.RootBone] : Vector3.zero;
            Vector3 fingers = open[mid] - wrist;
            Vector3 curl = closed[mid] - open[mid];
            Vector3 palm = Vector3.ProjectOnPlane(curl, fingers.normalized);
            Vector3 s = t.RootScale;
            fingers = Vector3.Scale(fingers, s);
            palm = Vector3.Scale(palm, s);
            if (fingers.sqrMagnitude > 1e-6f) t.FingerDir = fingers.normalized;
            if (palm.sqrMagnitude > 1e-8f) t.PalmDir = palm.normalized;
            t.PalmCenter = Vector3.Scale(midBase >= 0 ? (wrist + open[midBase]) * 0.5f : wrist, s);
        }

        /// <summary>
        /// World rotation for the hand root so that the fingers point along <paramref name="fingers"/> and the palm faces
        /// <paramref name="palm"/> (both world directions).
        /// </summary>
        public Quaternion RootRotationFor(Vector3 fingers, Vector3 palm)
        {
            Quaternion want = Quaternion.LookRotation(fingers, -palm);
            Quaternion have = Quaternion.LookRotation(FingerDir, -PalmDir);
            return want * Quaternion.Inverse(have);
        }
    }

    /// <summary>One puppet hand built from a template: bones, skinned mesh and per-finger bending.</summary>
    internal sealed class PuppetHand
    {
        public readonly GameObject Root;
        public readonly HandTemplate Template;
        private readonly Transform[] _nodes;
        private readonly Material[] _mats;
        private readonly float[] _shownBend = new float[5];
        private byte _skin = 255;

        public PuppetHand(HandTemplate t, Transform parent, string label)
        {
            Template = t;
            Root = new GameObject(label);
            Root.transform.SetParent(parent, false);
            Root.transform.localScale = t.RootScale;
            int n = t.Names.Count;
            _nodes = new Transform[n];
            _nodes[0] = Root.transform;
            for (int i = 1; i < n; i++)
            {
                var go = new GameObject(t.Names[i]);
                Transform x = go.transform;
                x.SetParent(_nodes[t.Parents[i]], false);
                x.localPosition = t.LPos[i];
                x.localRotation = t.LRot[i];
                x.localScale = t.LScale[i];
                _nodes[i] = x;
            }

            var rgo = _nodes[t.RendererNode].gameObject;
            var smr = rgo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = t.Mesh;
            var bones = new Transform[t.Bones.Length];
            for (int i = 0; i < bones.Length; i++) bones[i] = _nodes[t.Bones[i]];
            smr.bones = new Il2CppReferenceArray<Transform>(bones);
            if (t.RootBone > 0) smr.rootBone = _nodes[t.RootBone];
            smr.updateWhenOffscreen = true;
            _mats = new Material[t.Mats.Length];
            for (int i = 0; i < _mats.Length; i++) _mats[i] = t.Mats[i] != null ? new Material(t.Mats[i]) : null;
            smr.sharedMaterials = new Il2CppReferenceArray<Material>(_mats);
        }

        public void SetSkin(byte skin)
        {
            if (skin == _skin || string.IsNullOrEmpty(Template.SkinProperty)) return;
            _skin = skin;
            foreach (var m in _mats)
            {
                try { if (m != null && m.HasProperty(Template.SkinProperty)) m.SetFloat(Template.SkinProperty, skin / 255f); }
                catch (Exception) { }
            }
        }

        /// <summary>Bends the fingers towards the given values, smoothed a little so 60 Hz steps do not show.</summary>
        public void SetFingers(FingerBends b, float dt)
        {
            float k = 1f - Mathf.Exp(-25f * Mathf.Max(dt, 0.001f));
            var t = Template;
            for (int f = 0; f < 5; f++)
            {
                float target = b[f] / 255f;
                _shownBend[f] = Mathf.Lerp(_shownBend[f], target, k);
                var joints = t.FingerJoints[f];
                for (int j = 0; j < joints.Length; j++)
                    _nodes[joints[j]].localRotation = Quaternion.SlerpUnclamped(t.Open[f][j], t.Closed[f][j], _shownBend[f]);
            }
        }

        public void SetVisible(bool on)
        {
            if (Root.activeSelf != on) Root.SetActive(on);
        }
    }
}
