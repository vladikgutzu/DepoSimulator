using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// Points every carried weapon somewhere sensible instead of straight at the floor.
///
/// With CarryRotation left at zero the model hangs on the socket in whatever orientation it was
/// modelled in, and these were all modelled along one axis — so every one of them dangled
/// vertically, blade down, like a pendulum on the chest.
///
/// Rather than typing three angles per weapon, the shaft is measured: the far end of the model
/// from its grip point IS the shaft direction, whatever axis the artist used. The rotation written
/// is the one that takes that direction onto a carry angle — forward and down, the way a man
/// walks with a bat — so a crowbar and a sledgehammer end up hanging the same way without either
/// of them being measured by hand.
public static class AimCarriedWeapons
{
    /// In socket space, which is the chest's: forward and 40° down.
    static readonly Vector3 CarryAim = new Vector3(0f, -0.64f, 0.77f).normalized;

    [MenuItem("Tools/Warehouse/Aim Carried Weapons")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var report = new StringBuilder();
        var defs = Resources.LoadAll<MeleeWeaponDefinition>("Catalog/Weapons")
                            .Where(d => d != null && d.Prefab != null)
                            .OrderBy(d => d.name);

        int done = 0;
        foreach (var def in defs)
        {
            if (!ShaftDirection(def, out Vector3 shaft, out float length))
            {
                report.AppendLine($"• {def.name}: no geometry to measure — left alone");
                continue;
            }

            var rot = Quaternion.FromToRotation(shaft, CarryAim);

            def.CarryRotation = rot.eulerAngles;
            EditorUtility.SetDirty(def);
            done++;

            report.AppendLine($"• {def.name}: shaft {shaft} ({length:F2} m) -> CarryRotation {def.CarryRotation}");
        }

        AssetDatabase.SaveAssets();
        report.AppendLine($"\n{done} weapons aimed. Drag them off this in play and save as usual "
                        + "if a particular one wants its own angle.");
        return report.ToString();
    }

    /// The way the weapon points, in its own space: from the grip to the vertex furthest from it.
    static bool ShaftDirection(MeleeWeaponDefinition def, out Vector3 dir, out float length)
    {
        dir = Vector3.forward;
        length = 0f;

        var anchors = def.Prefab.GetComponent<WeaponGripAnchors>();
        Vector3 grip = anchors != null && anchors.MainGrip != null
            ? anchors.MainGrip.localPosition
            : Vector3.zero;

        Vector3 far = grip;
        float farthest = 0f;

        foreach (var mf in def.Prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;

            // Prefab-space: the anchors are expressed there too, so grip and geometry agree.
            var m = def.Prefab.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
            foreach (var v in mf.sharedMesh.vertices)
            {
                Vector3 p = m.MultiplyPoint3x4(v);
                float d = (p - grip).sqrMagnitude;
                if (d <= farthest) continue;
                farthest = d;
                far = p;
            }
        }

        if (farthest < 1e-4f) return false;

        dir    = (far - grip).normalized;
        length = Mathf.Sqrt(farthest);
        return true;
    }
}
