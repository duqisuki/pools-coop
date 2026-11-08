using System;
using PoolsCoop.Net;
using UnityEngine;

namespace PoolsCoop
{
    /// <summary>
    /// Reads the local player for the pose packet. VR: the head camera and the game's own physics hands (the hand model's root, where it
    /// really is, including when it rests against a wall), the bend of every finger from Autohand, the chosen hand model and skin tone.
    /// Flat screen: the camera, plus hands that hang at the sides and swing while walking, and the right hand reaches out while the
    /// mouse button is held (interacting), so a VR friend sees the same kind of hands a VR player has.
    /// </summary>
    internal static class LocalRig
    {
        private static Autohand.Hand _left, _right;
        private static float _nextHandSearch;
        private static byte _skin = 128;
        private static float _nextSkinRead;

        // flat walking
        private static Vector3 _lastFeet;
        private static float _lastT = -1f;
        private static float _speed;
        private static float _phase;
        private static float _reach;

        private static void FindHands(float now)
        {
            if (now < _nextHandSearch && _left != null && _right != null && _left.isActiveAndEnabled && _right.isActiveAndEnabled) return;
            _nextHandSearch = now + 1f;
            _left = _right = null;
            foreach (var h in UnityEngine.Object.FindObjectsByType<Autohand.Hand>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (h == null || !h.gameObject.name.Contains("AutoHand")) continue;
                if (h.left) _left = h; else _right = h;
            }
        }

        private static FingerBends Bends(Autohand.Hand h)
        {
            var b = FingerBends.All(40);
            try
            {
                foreach (var f in h.fingers)
                {
                    if (f == null) continue;
                    int slot;
                    switch (f.fingerType)
                    {
                        case Autohand.FingerEnum.thumb: slot = 0; break;
                        case Autohand.FingerEnum.index: slot = 1; break;
                        case Autohand.FingerEnum.middle: slot = 2; break;
                        case Autohand.FingerEnum.ring: slot = 3; break;
                        case Autohand.FingerEnum.pinky: slot = 4; break;
                        default: continue;
                    }
                    b[slot] = (byte)Mathf.RoundToInt(Mathf.Clamp01(f.GetCurrentBend()) * 255f);
                }
            }
            catch (Exception) { }
            return b;
        }

        private static void ReadSkin(Autohand.Hand h, float now)
        {
            if (now < _nextSkinRead || h == null) return;
            _nextSkinRead = now + 3f;
            try
            {
                string prop = "";
                if (Backrooms.Features.VR.VRHandsMaterialManager.IsInstanced) prop = Backrooms.Features.VR.VRHandsMaterialManager.Instance.skinTonePropertyName;
                if (string.IsNullOrEmpty(prop)) return;
                var r = h.GetComponentInChildren<SkinnedMeshRenderer>();
                if (r == null) return;
                var m = r.material;
                if (m != null && m.HasProperty(prop)) _skin = (byte)Mathf.RoundToInt(Mathf.Clamp01(m.GetFloat(prop)) * 255f);
            }
            catch (Exception) { }
        }

        private static NetPose From(Transform t) => new NetPose(ToVec(t.position), ToQuat(t.rotation));
        private static Vec3 ToVec(Vector3 v) => new Vec3(v.x, v.y, v.z);
        private static Quat ToQuat(Quaternion q) => new Quat(q.x, q.y, q.z, q.w);

        /// <summary>The VR pose, or false when the rig is not ready.</summary>
        public static bool TryVr(float now, out PlayerPose pose)
        {
            pose = new PlayerPose();
            if (!Backrooms.Features.VR.PoolsXROrigin.IsInstanced) return false;
            var o = Backrooms.Features.VR.PoolsXROrigin.Instance;
            var cam = o.xrOrigin != null ? o.xrOrigin.Camera : null;
            if (cam == null) return false;
            FindHands(now);
            pose.Head = From(cam.transform);
            pose.Flags = PoseFlags.Vr;
            if (_left != null && _right != null)
            {
                pose.Left = From(_left.transform);
                pose.Right = From(_right.transform);
                pose.LeftFingers = Bends(_left);
                pose.RightFingers = Bends(_right);
                pose.Flags |= PoseFlags.HandsAreRoots;
                if (_left.gameObject.name.IndexOf("Female", StringComparison.OrdinalIgnoreCase) >= 0) pose.Flags |= PoseFlags.FemaleHands;
                ReadSkin(_left, now);
            }
            else if (o.leftMotionControllerTracking != null && o.rightMotionControllerTracking != null)
            {
                pose.Left = From(o.leftMotionControllerTracking.transform);
                pose.Right = From(o.rightMotionControllerTracking.transform);
                pose.LeftFingers = pose.RightFingers = FingerBends.All(60);
            }
            else return false;
            pose.Skin = _skin;
            return true;
        }

        /// <summary>The flat-screen pose: camera head and natural hands.</summary>
        public static bool TryFlat(float now, out PlayerPose pose)
        {
            pose = new PlayerPose();
            Camera cam = CoopBehaviour.FlatCamera();
            if (cam == null) return false;
            Transform c = cam.transform;
            pose.Head = From(c);

            // walking speed from the body (or the camera when there is no body)
            Vector3 feet = FPSController.IsInstanced ? FPSController.Instance.transform.position : c.position;
            float dt = _lastT < 0f ? 0f : now - _lastT;
            if (dt > 0f && dt < 0.5f)
            {
                Vector3 d = feet - _lastFeet; d.y = 0f;
                float v = d.magnitude / dt;
                if (v > 20f) v = 0f; // teleport
                _speed = Mathf.Lerp(_speed, v, 1f - Mathf.Exp(-8f * dt));
            }
            _lastFeet = feet;
            _lastT = now;
            bool moving = _speed > 0.4f;
            _phase += dt * Mathf.Lerp(0f, 7.5f, Mathf.Clamp01(_speed / 3f));
            if (moving) pose.Flags |= PoseFlags.Moving;

            bool interact = false;
            try
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                interact = mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed) && Cursor.lockState == CursorLockMode.Locked;
            }
            catch (Exception) { }
            _reach = Mathf.MoveTowards(_reach, interact ? 1f : 0f, dt * 5f);

            Vector3 fwd = c.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = c.up; fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            float swing = Mathf.Sin(_phase) * Mathf.Clamp01(_speed / 2.5f);

            pose.Left = FlatHand(true, c, fwd, right, -swing, 0f);
            pose.Right = FlatHand(false, c, fwd, right, swing, _reach);
            pose.LeftFingers = FingerBends.All(70);
            pose.RightFingers = FingerBends.Lerp(FingerBends.All(70), new FingerBends(120, 90, 170, 190, 200), _reach);
            pose.Flags |= PoseFlags.HandsAreRoots;
            pose.Skin = _skin;
            return true;
        }

        private static NetPose FlatHand(bool left, Transform cam, Vector3 fwd, Vector3 right, float swing, float reach)
        {
            float side = left ? -1f : 1f;
            Vector3 head = cam.position;
            // hanging at the side, a little in front of the hip, swinging forwards and back
            Vector3 hang = head + Vector3.down * 0.78f + right * (0.25f * side) + fwd * (0.05f + 0.22f * swing);
            Vector3 hangFingers = (Vector3.down + fwd * (0.45f * swing)).normalized;
            Vector3 hangPalm = right * -side; // palms towards the body
            // reaching: in front of the camera, palm forward and down a little
            Vector3 reachPos = head + cam.forward * 0.42f + right * (0.16f * side) + Vector3.down * 0.18f;
            Vector3 reachFingers = cam.forward;
            Vector3 reachPalm = (cam.forward * 0.6f - cam.up * 0.4f + right * (-0.3f * side)).normalized;

            Vector3 pos = Vector3.Lerp(hang, reachPos, reach);
            Vector3 fingers = Vector3.Slerp(hangFingers, reachFingers, reach);
            Vector3 palm = Vector3.Slerp(hangPalm, reachPalm, reach);

            var t = HandTemplate.Get(left, false);
            Quaternion rot;
            if (t != null)
            {
                rot = t.RootRotationFor(fingers, palm);
                pos -= rot * t.PalmCenter; // put the middle of the hand, not the wrist bone, on the point
            }
            else rot = Quaternion.LookRotation(fingers, -palm);
            return new NetPose(ToVec(pos), ToQuat(rot));
        }

        /// <summary>World positions of the local player's real hands (VR), for touching objects.</summary>
        public static bool TryHandPositions(out Vector3 left, out Vector3 right)
        {
            left = right = Vector3.zero;
            FindHands(Time.realtimeSinceStartup);
            if (_left == null || _right == null) return false;
            left = _left.transform.position;
            right = _right.transform.position;
            return true;
        }

        /// <summary>Rigidbodies the local player's hands are holding right now (VR grab).</summary>
        public static void HeldBodies(System.Collections.Generic.List<Rigidbody> into)
        {
            into.Clear();
            FindHands(Time.realtimeSinceStartup);
            foreach (var h in new[] { _left, _right })
            {
                try
                {
                    if (h == null || !h.IsHolding()) continue;
                    var g = h.holdingObj;
                    if (g != null && g.body != null) into.Add(g.body);
                }
                catch (Exception) { }
            }
        }
    }
}
