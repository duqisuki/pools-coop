using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PoolsCoop
{
    /// <summary>Read-only scene survey for the recon test mode: what physics objects, grab components, water and UI a scene has.</summary>
    internal static class Recon
    {
        public static string OutDir
        {
            get
            {
                string d = Path.Combine(BepInEx.Paths.BepInExRootPath, "recon");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static void LogBuildScenes()
        {
            var sb = new StringBuilder();
            int n = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < n; i++)
                sb.Append(i).Append('=').Append(Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i))).Append("  ");
            Plugin.Logger.LogInfo($"recon: {n} scenes in the build: {sb}");
        }

        private static int Count<T>() where T : UnityEngine.Object
        {
            return UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string Components(GameObject go)
        {
            var names = new List<string>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                string n;
                try { n = c.GetIl2CppType().Name; } catch (Exception) { n = "?"; }
                if (n != "Transform" && n != "MeshFilter" && n != "MeshRenderer") names.Add(n);
            }
            return string.Join(",", names);
        }

        private static void DumpTree(StringBuilder sb, Transform t, int depth, int maxDepth)
        {
            string pad = new string(' ', depth * 2);
            string extra = "";
            try
            {
                var r = t.GetComponent<Renderer>();
                if (r != null)
                {
                    var mats = new List<string>();
                    foreach (var m in r.sharedMaterials) mats.Add(m == null ? "null" : $"{m.name}<{(m.shader != null ? m.shader.name : "?")}>");
                    string mesh = "";
                    var smr = r.TryCast<SkinnedMeshRenderer>();
                    if (smr != null && smr.sharedMesh != null) mesh = $" skinned mesh='{smr.sharedMesh.name}' v={smr.sharedMesh.vertexCount} bones={smr.bones.Length} root='{(smr.rootBone != null ? smr.rootBone.name : "")}'";
                    var mf = t.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) mesh = $" mesh='{mf.sharedMesh.name}' v={mf.sharedMesh.vertexCount}";
                    extra = $" R[{r.GetIl2CppType().Name} en={r.enabled}{mesh} mats={string.Join(";", mats)}]";
                }
            }
            catch (Exception e) { extra = " R[err " + e.GetType().Name + "]"; }
            Vector3 lp = t.localPosition, ls = t.localScale;
            sb.AppendLine(FormattableString.Invariant($"{pad}{t.name} act={t.gameObject.activeSelf} lp=({lp.x:F3},{lp.y:F3},{lp.z:F3}) ls=({ls.x:F2},{ls.y:F2},{ls.z:F2}) | {Components(t.gameObject)}{extra}"));
            if (depth >= maxDepth) { if (t.childCount > 0) sb.AppendLine($"{pad}  ... {t.childCount} children"); return; }
            for (int i = 0; i < t.childCount; i++) DumpTree(sb, t.GetChild(i), depth + 1, maxDepth);
        }

        private static void DumpRig(StringBuilder sb)
        {
            var seen = new HashSet<IntPtr>();
            void Root(string why, Transform t, int maxDepth)
            {
                if (t == null || !seen.Add(t.Pointer)) return;
                sb.AppendLine($"-- {why}: {PathOf(t)}");
                DumpTree(sb, t, 0, maxDepth);
            }
            foreach (var h in UnityEngine.Object.FindObjectsByType<Backrooms.Features.VR.VRHandModelSelector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Root("VRHandModelSelector", h.transform, 7);
            foreach (var h in UnityEngine.Object.FindObjectsByType<Autohand.Hand>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Root("Autohand.Hand", h.transform, 7);
            foreach (var a in UnityEngine.Object.FindObjectsByType<AnimatedHands>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Root("AnimatedHands", a.transform, 6);
            foreach (var a in UnityEngine.Object.FindObjectsByType<XRControllerHandsAnimatorManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Root("XRControllerHandsAnimatorManager", a.transform, 6);
            foreach (var f in UnityEngine.Object.FindObjectsByType<FPSController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Root("FPSController", f.transform, 4);
            foreach (var o in UnityEngine.Object.FindObjectsByType<Backrooms.Features.VR.PoolsXROrigin>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Root("PoolsXROrigin", o.transform, 3);
            foreach (var fp in UnityEngine.Object.FindObjectsByType<Backrooms.Features.VR.VRFootstepPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"-- VRFootstepPlayer at {PathOf(fp.transform)} effect={(fp.waterSplashEffect != null ? fp.waterSplashEffect.name : "none")}");
            sb.AppendLine("-- skinned renderers in the scene:");
            foreach (var smr in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                sb.AppendLine($"   {PathOf(smr.transform)} mesh='{(smr.sharedMesh != null ? smr.sharedMesh.name : "")}' active={smr.gameObject.activeInHierarchy}");
            sb.AppendLine("-- loaded meshes with hand/arm/body/foot in the name:");
            foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                string n = m.name.ToLowerInvariant();
                if (n.Contains("hand") || n.Contains("arm") || n.Contains("body") || n.Contains("foot") || n.Contains("leg") || n.Contains("player"))
                    sb.AppendLine($"   mesh '{m.name}' v={m.vertexCount}");
            }
        }

        public static void DumpScene(string label)
        {
            var sb = new StringBuilder();
            Scene scene = SceneManager.GetActiveScene();
            sb.AppendLine($"scene '{scene.name}' buildIndex={scene.buildIndex} roots={scene.rootCount} label={label}");

            sb.AppendLine("== component counts (including inactive)");
            void Add(string name, Func<int> f)
            {
                try { sb.AppendLine($"{name}: {f()}"); }
                catch (Exception e) { sb.AppendLine($"{name}: error {e.GetType().Name}"); }
            }
            Add("Rigidbody", Count<Rigidbody>);
            Add("Collider", Count<Collider>);
            Add("Autohand.Grabbable", Count<Autohand.Grabbable>);
            Add("Autohand.Hand", Count<Autohand.Hand>);
            Add("Backrooms.Features.VR.SoftGrabInteractable", Count<Backrooms.Features.VR.SoftGrabInteractable>);
            Add("XRBaseInteractable", Count<UnityEngine.XR.Interaction.Toolkit.Interactables.XRBaseInteractable>);
            Add("Door", Count<Door>);
            Add("HotelDoor", Count<HotelDoor>);
            Add("Chair", Count<Chair>);
            Add("Collectable", Count<Backrooms.Features.Collectables.Collectable>);
            Add("BuoyancyObject", Count<Backrooms.Features.Buoyancy.BuoyancyObject>);
            Add("RigidbodyRippleSpawner", Count<Backrooms.Features.WaterDeform.RigidbodyRippleSpawner>);
            Add("WaterBodyVolume", Count<WaterBodyVolume>);
            Add("WaterSlide", Count<WaterSlide>);
            Add("Ladder", Count<Ladder>);
            Add("Canvas", Count<Canvas>);
            Add("UI.Button", Count<UnityEngine.UI.Button>);

            sb.AppendLine("== rigidbodies");
            try
            {
                var rows = new List<string>();
                int nonKin = 0;
                foreach (var rb in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (!rb.isKinematic) nonKin++;
                    Vector3 p = rb.position;
                    rows.Add(FormattableString.Invariant(
                        $"{PathOf(rb.transform)} | kin={rb.isKinematic} mass={rb.mass:F2} grav={rb.useGravity} active={rb.gameObject.activeInHierarchy} pos=({p.x:F1},{p.y:F1},{p.z:F1}) | {Components(rb.gameObject)}"));
                }
                rows.Sort(StringComparer.Ordinal);
                sb.AppendLine($"non-kinematic: {nonKin} of {rows.Count}");
                foreach (var r in rows) sb.AppendLine(r);
            }
            catch (Exception e) { sb.AppendLine("rigidbody dump failed: " + e); }

            sb.AppendLine("== canvases");
            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    Vector3 p = c.transform.position, s = c.transform.lossyScale;
                    sb.AppendLine(FormattableString.Invariant(
                        $"{PathOf(c.transform)} | mode={c.renderMode} active={c.gameObject.activeInHierarchy} pos=({p.x:F2},{p.y:F2},{p.z:F2}) scale=({s.x:F4},{s.y:F4},{s.z:F4})"));
                }
            }
            catch (Exception e) { sb.AppendLine("canvas dump failed: " + e); }

            sb.AppendLine("== buttons");
            try
            {
                foreach (var b in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    string text = "";
                    try
                    {
                        var tmp = b.GetComponentInChildren<TMPro.TMP_Text>(true);
                        if (tmp != null) text = tmp.text;
                    }
                    catch (Exception) { }
                    sb.AppendLine($"{PathOf(b.transform)} | interactable={b.interactable} active={b.gameObject.activeInHierarchy} text='{text}' | {Components(b.gameObject)}");
                }
            }
            catch (Exception e) { sb.AppendLine("button dump failed: " + e); }

            sb.AppendLine("== player rig (hands, bodies, renderers)");
            try { DumpRig(sb); }
            catch (Exception e) { sb.AppendLine("rig dump failed: " + e); }

            string file = Path.Combine(OutDir, $"recon-{scene.name}-{label}.txt");
            File.WriteAllText(file, sb.ToString());
            Plugin.Logger.LogInfo($"recon: wrote {file} ({sb.Length} chars)");
        }
    }
}
