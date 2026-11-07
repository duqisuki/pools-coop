using System;
using PoolsCoop.Net;
using UnityEngine;

namespace PoolsCoop
{
    /// <summary>A stand-in body for a remote player: a sphere for the head and two cubes for the hands.</summary>
    internal sealed class Puppet
    {
        private static Shader _shader;
        private static bool _shaderTried;

        private readonly GameObject _root;
        private readonly Transform _head;
        private readonly Transform _left;
        private readonly Transform _right;
        private readonly Transform _torso;
        private readonly PuppetWater _water = new PuppetWater();
        public Puppet(byte id, string name)
        {
            _root = new GameObject($"CoopPuppet_{id}_{name}");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            Color color = ColorFor(id);
            _head = Make(PrimitiveType.Sphere, "head", new Vector3(0.22f, 0.22f, 0.22f), color);
            _left = Make(PrimitiveType.Cube, "left", new Vector3(0.09f, 0.09f, 0.14f), color);
            _right = Make(PrimitiveType.Cube, "right", new Vector3(0.09f, 0.09f, 0.14f), color);
            _torso = Make(PrimitiveType.Capsule, "torso", new Vector3(0.3f, 0.36f, 0.3f), color);
            Plugin.Logger.LogInfo($"puppet created for player {id} '{name}'");
        }

        /// <summary>Puts the puppet exactly on the pose. The pose is already interpolated by the PoseBuffer, so no further smoothing here.</summary>
        public void Apply(PlayerPose p)
        {
            Move(_head, p.Head, 1f);
            Move(_left, p.Left, 1f);
            Move(_right, p.Right, 1f);
            if (_torso != null)
            {
                // A body under the head so a flat-screen friend (head only) does not look like a floating ball: stays upright, follows the head's yaw.
                float yaw = _head.rotation.eulerAngles.y;
                _torso.position = _head.position + new Vector3(0f, -0.5f, 0f);
                _torso.rotation = Quaternion.Euler(0f, yaw, 0f);
            }
        }

        /// <summary>Splashes when this player's head or hands cross a water surface.</summary>
        public void WaterTick(PlayerPose drawn, float now) => _water.Tick(drawn, now);

        /// <summary>Largest distance between the drawn head/hands and the given pose, in metres.</summary>
        public float MaxDistanceTo(PlayerPose p)
        {
            float a = Dist(_head.position, p.Head.Pos), b = Dist(_left.position, p.Left.Pos), c = Dist(_right.position, p.Right.Pos);
            return Math.Max(a, Math.Max(b, c));
        }

        private static float Dist(Vector3 v, Vec3 w)
        {
            float dx = v.x - w.X, dy = v.y - w.Y, dz = v.z - w.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public void Destroy()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
        }

        private static void Move(Transform t, NetPose target, float k)
        {
            var pos = new Vector3(target.Pos.X, target.Pos.Y, target.Pos.Z);
            var rot = new Quaternion(target.Rot.X, target.Rot.Y, target.Rot.Z, target.Rot.W);
            t.position = Vector3.Lerp(t.position, pos, k);
            t.rotation = Quaternion.Slerp(t.rotation, rot, k);
        }

        private Transform Make(PrimitiveType type, string label, Vector3 scale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = label;
            go.transform.SetParent(_root.transform, false);
            go.transform.localScale = scale;

            // No collider: a puppet must never push the real player or block physics objects.
            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);

            var rend = go.GetComponent<Renderer>();
            if (rend != null) ApplyColor(rend, color);
            return go.transform;
        }

        private static void ApplyColor(Renderer rend, Color color)
        {
            if (!_shaderTried)
            {
                _shaderTried = true;
                foreach (string n in new[] { "HDRP/Unlit", "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default", "HDRP/Lit" })
                {
                    Shader s = Shader.Find(n);
                    if (s != null) { _shader = s; Plugin.Logger.LogInfo($"puppet shader: {n}"); break; }
                }
                if (_shader == null) Plugin.Logger.LogWarning("puppet shader: none found, using the primitive's default material");
            }

            if (_shader != null)
            {
                var m = new Material(_shader);
                m.color = color;
                try { m.SetColor("_UnlitColor", color); } catch (Exception) { }
                try { m.SetColor("_BaseColor", color); } catch (Exception) { }
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
                case 0: return new Color(0.2f, 0.8f, 1.0f, 1f);
                case 1: return new Color(1.0f, 0.6f, 0.1f, 1f);
                case 2: return new Color(0.3f, 1.0f, 0.4f, 1f);
                default: return new Color(1.0f, 0.3f, 0.8f, 1f);
            }
        }
    }
}
