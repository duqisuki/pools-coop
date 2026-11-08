using System;
using PoolsCoop.Net;
using UnityEngine;

namespace PoolsCoop
{
    /// <summary>
    /// A remote player: the game's own VR hands (skinned, with fingers that bend as the friend's fingers do), a head and a torso,
    /// and a name above the head. A flat-screen friend gets the same hands, hanging at the sides and swinging as they walk.
    /// When the hand meshes cannot be found (should not happen: the rig is in every chapter of both builds) small boxes stand in.
    /// </summary>
    internal sealed class Puppet
    {
        private static Shader _lit, _unlit;
        private static bool _shaderTried;

        private readonly byte _id;
        private readonly GameObject _root;
        private readonly Transform _head;
        private readonly Transform _torso;
        private readonly Transform _neck;
        private readonly Transform _fallbackLeft, _fallbackRight;
        private readonly Transform _label;
        private PuppetHand _left, _right;
        private bool _female;
        private float _lastApply = -1f;
        private readonly PuppetWater _water = new PuppetWater();
        public PlayerPose LastPose;

        public Puppet(byte id, string name)
        {
            _id = id;
            _root = new GameObject($"CoopPuppet_{id}_{name}");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Color color = ColorFor(id);
            Color dark = Color.Lerp(color, Color.black, 0.55f);
            _head = Make(PrimitiveType.Sphere, "head", new Vector3(0.19f, 0.23f, 0.21f), dark, true);
            _neck = Make(PrimitiveType.Cylinder, "neck", new Vector3(0.09f, 0.06f, 0.09f), dark, true);
            _torso = Make(PrimitiveType.Capsule, "torso", new Vector3(0.36f, 0.33f, 0.22f), color, true);
            _fallbackLeft = Make(PrimitiveType.Cube, "left", new Vector3(0.08f, 0.03f, 0.16f), dark, true);
            _fallbackRight = Make(PrimitiveType.Cube, "right", new Vector3(0.08f, 0.03f, 0.16f), dark, true);
            _label = MakeLabel(name, color);
            Plugin.Logger.LogInfo($"puppet created for player {id} '{name}'");
        }

        private void EnsureHands(bool female)
        {
            if (_left != null && _right != null && female == _female && _left.Root != null && _right.Root != null) return;
            var tl = HandTemplate.Get(true, female);
            var tr = HandTemplate.Get(false, female);
            if (tl == null || tr == null) return;
            if (_left != null && tl == _left.Template && tr == _right.Template) return; // the wanted model is missing, keep what we have
            try
            {
                if (_left != null) UnityEngine.Object.Destroy(_left.Root);
                if (_right != null) UnityEngine.Object.Destroy(_right.Root);
                _left = new PuppetHand(tl, _root.transform, "hand_left");
                _right = new PuppetHand(tr, _root.transform, "hand_right");
                _female = female;
                _fallbackLeft.gameObject.SetActive(false);
                _fallbackRight.gameObject.SetActive(false);
                Plugin.Logger.LogInfo($"puppet {_id}: real hands built ({tl.Name}, {tr.Name})");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"puppet {_id}: building hands failed: {e.Message}");
                _left = _right = null;
            }
        }

        /// <summary>Puts the puppet exactly on the pose. The pose is already interpolated by the PoseBuffer, so no further smoothing here.</summary>
        public void Apply(PlayerPose p)
        {
            LastPose = p;
            float now = Time.realtimeSinceStartup;
            float dt = _lastApply < 0f ? 0.016f : Mathf.Clamp(now - _lastApply, 0f, 0.2f);
            _lastApply = now;

            Vector3 headPos = V(p.Head.Pos);
            Quaternion headRot = Q(p.Head.Rot);
            _head.position = headPos;
            _head.rotation = headRot;

            // Body under the head, upright, turned with the head's yaw (a VR player's real body is not tracked).
            Vector3 fwd = headRot * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) { fwd = headRot * Vector3.up; fwd.y = 0f; }
            Quaternion yaw = fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd.normalized, Vector3.up) : Quaternion.identity;
            _neck.position = headPos + Vector3.down * 0.17f - yaw * Vector3.forward * 0.02f;
            _neck.rotation = yaw;
            _torso.position = headPos + Vector3.down * 0.52f - yaw * Vector3.forward * 0.04f;
            _torso.rotation = yaw;

            bool female = (p.Flags & PoseFlags.FemaleHands) != 0;
            EnsureHands(female);
            bool roots = (p.Flags & PoseFlags.HandsAreRoots) != 0;
            if (_left != null && _right != null)
            {
                PlaceHand(_left, p.Left, roots, true);
                _anchorL = V(p.Left.Pos);
                _anchorR = V(p.Right.Pos);
                PlaceHand(_right, p.Right, roots, false);
                bool extended = (p.Flags & PoseFlags.Extended) != 0;
                _left.SetFingers(extended ? p.LeftFingers : FingerBends.All(60), dt);
                _right.SetFingers(extended ? p.RightFingers : FingerBends.All(60), dt);
                if (extended) { _left.SetSkin(p.Skin); _right.SetSkin(p.Skin); }
            }
            else
            {
                Place(_fallbackLeft, p.Left);
                Place(_fallbackRight, p.Right);
            }

            if (_label != null)
            {
                _label.position = headPos + Vector3.up * 0.28f;
                Camera cam = ViewCamera();
                if (cam != null) _label.rotation = Quaternion.LookRotation(_label.position - cam.transform.position, Vector3.up);
            }
        }

        private Vector3 _anchorL, _anchorR; // where the pose put each hand, before the wrist offset (for the bot metric)

        private static void PlaceHand(PuppetHand h, NetPose pose, bool isRoot, bool left)
        {
            Vector3 pos = V(pose.Pos);
            Quaternion rot = Q(pose.Rot);
            if (!isRoot)
            {
                // An older sender sent the controller pose: the game's hand sits about 16 cm behind the controller origin
                // (Hand Follow Target) with the fingers along the controller's forward and the palm inwards.
                Vector3 fingers = rot * Vector3.forward;
                Vector3 palm = rot * (left ? Vector3.right : Vector3.left);
                Quaternion r = h.Template.RootRotationFor(fingers, palm);
                pos = pos + rot * new Vector3(left ? -0.022f : 0.022f, -0.007f, -0.158f);
                rot = r;
            }
            h.Root.transform.SetPositionAndRotation(pos, rot);
        }

        private static void Place(Transform t, NetPose target)
        {
            t.position = V(target.Pos);
            t.rotation = Q(target.Rot);
        }

        /// <summary>Splashes when this player's head or hands cross a water surface.</summary>
        public void WaterTick(PlayerPose drawn, float now) => _water.Tick(drawn, now);

        /// <summary>Largest distance between the drawn head/hands and the given pose, in metres.</summary>
        public float MaxDistanceTo(PlayerPose p)
        {
            Vector3 l = _left != null ? _anchorL : _fallbackLeft.position;
            Vector3 r = _right != null ? _anchorR : _fallbackRight.position;
            float a = Dist(_head.position, p.Head.Pos), b = Dist(l, p.Left.Pos), c = Dist(r, p.Right.Pos);
            return Math.Max(a, Math.Max(b, c));
        }

        private static float Dist(Vector3 v, Vec3 w)
        {
            float dx = v.x - w.X, dy = v.y - w.Y, dz = v.z - w.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public bool HasRealHands => _left != null && _right != null;

        public void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }

        private static Vector3 V(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
        private static Quaternion Q(Quat q) => new Quaternion(q.X, q.Y, q.Z, q.W);

        private static Camera ViewCamera()
        {
            try
            {
                if (Plugin.IsVrActive && Backrooms.Features.VR.PoolsXROrigin.IsInstanced)
                {
                    var o = Backrooms.Features.VR.PoolsXROrigin.Instance;
                    if (o.xrOrigin != null && o.xrOrigin.Camera != null) return o.xrOrigin.Camera;
                }
            }
            catch (Exception) { }
            return CoopBehaviour.FlatCamera();
        }

        private Transform Make(PrimitiveType type, string label, Vector3 scale, Color color, bool lit)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = label;
            go.transform.SetParent(_root.transform, false);
            go.transform.localScale = scale;

            // No collider: a puppet must never push the real player or block physics objects.
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);

            var rend = go.GetComponent<Renderer>();
            if (rend != null) ApplyColor(rend, color, lit);
            return go.transform;
        }

        private Transform MakeLabel(string name, Color color)
        {
            try
            {
                var go = new GameObject("name");
                go.transform.SetParent(_root.transform, false);
                var tmp = go.AddComponent<TMPro.TextMeshPro>();
                var font = CoopPanel.FontAsset();
                if (font != null) tmp.font = font;
                tmp.text = name;
                tmp.fontSize = 1.1f;
                tmp.alignment = TMPro.TextAlignmentOptions.Center;
                tmp.color = Color.Lerp(color, Color.white, 0.5f);
                tmp.enableWordWrapping = false;
                tmp.rectTransform.sizeDelta = new Vector2(3f, 0.4f);
                return go.transform;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"puppet: name label failed: {e.Message}");
                return null;
            }
        }

        private static void FindShaders()
        {
            if (_shaderTried) return;
            _shaderTried = true;
            foreach (string n in new[] { "HDRP/Lit", "Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit" })
            {
                Shader s = Shader.Find(n);
                if (s != null && s.isSupported) { _lit = s; Plugin.Logger.LogInfo($"puppet lit shader: {n}"); break; }
            }
            foreach (string n in new[] { "HDRP/Unlit", "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default" })
            {
                Shader s = Shader.Find(n);
                if (s != null) { _unlit = s; Plugin.Logger.LogInfo($"puppet unlit shader: {n}"); break; }
            }
            if (_lit == null && _unlit == null) Plugin.Logger.LogWarning("puppet shader: none found, using the primitive's default material");
        }

        private static void ApplyColor(Renderer rend, Color color, bool lit)
        {
            FindShaders();
            Shader sh = lit && _lit != null ? _lit : (_unlit ?? _lit);
            if (sh != null)
            {
                var m = new Material(sh);
                m.color = color;
                try { m.SetColor("_UnlitColor", color); } catch (Exception) { }
                try { m.SetColor("_BaseColor", color); } catch (Exception) { }
                try { m.SetFloat("_Smoothness", 0.25f); } catch (Exception) { }
                rend.material = m;
            }
            else
            {
                rend.material.color = color;
            }
        }

        private static Color ColorFor(byte id)
        {
            switch (id % 4)
            {
                case 0: return new Color(0.2f, 0.65f, 0.9f, 1f);
                case 1: return new Color(0.95f, 0.55f, 0.15f, 1f);
                case 2: return new Color(0.3f, 0.85f, 0.4f, 1f);
                default: return new Color(0.9f, 0.3f, 0.75f, 1f);
            }
        }
    }
}
