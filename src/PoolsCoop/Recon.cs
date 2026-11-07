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

            string file = Path.Combine(OutDir, $"recon-{scene.name}-{label}.txt");
            File.WriteAllText(file, sb.ToString());
            Plugin.Logger.LogInfo($"recon: wrote {file} ({sb.Length} chars)");
        }
    }
}
