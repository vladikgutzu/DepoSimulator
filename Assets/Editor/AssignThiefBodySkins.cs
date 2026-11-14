using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// Fills ThiefController._bodyVariants on the existing Thief prefab.
///
/// Separate from Setup Thief System on purpose: that rebuilds the prefab from the source body
/// and would reset every tuned value on it back to the code defaults. This only writes the one
/// field, so a prefab that has been balanced by hand keeps its numbers.
public static class AssignThiefBodySkins
{
    const string ThiefPrefabPath = "Assets/Prefabs/Thief.prefab";
    const string MaterialFolder  = "Assets/Zombie Slaughter/Materials";

    [MenuItem("Tools/Warehouse/Assign Thief Body Skins")]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ThiefPrefabPath);
        if (prefab == null) return "prefab not found: " + ThiefPrefabPath;

        var thief = prefab.GetComponent<ThiefController>();
        if (thief == null) return "no ThiefController on " + ThiefPrefabPath;

        // Which mesh family the prefab was built on decides which skins fit its skeleton:
        // A-E of one family share a mesh, so only those are interchangeable.
        var smr = prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
        if (smr == null || smr.sharedMaterial == null) return "no skinned body to read a family off";

        string current = smr.sharedMaterial.name;               // e.g. "Zombie Guy 2A"
        string family  = current.Substring(0, current.Length - 1);

        var mats = new List<Material>();
        foreach (char v in "ABCDE")
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/{family}{v}.mat");
            if (mat != null) mats.Add(mat);
        }

        if (mats.Count == 0) return $"no \"{family}x\" materials in {MaterialFolder}";

        var so  = new SerializedObject(thief);
        var arr = so.FindProperty("_bodyVariants");
        if (arr == null) return "_bodyVariants not found — is ThiefController compiled?";

        arr.arraySize = mats.Count;
        for (int i = 0; i < mats.Count; i++)
            arr.GetArrayElementAtIndex(i).objectReferenceValue = mats[i];

        so.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(prefab);
        AssetDatabase.SaveAssets();

        return $"assigned {mats.Count} skins from family '{family}' " +
               $"(body was {current}): {string.Join(", ", mats.ConvertAll(m => m.name))}";
    }
}
