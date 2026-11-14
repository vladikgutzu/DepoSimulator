using UnityEngine;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;

public class FixAssemblyPallet
{
    public static void Execute()
    {
        var bench = Object.FindAnyObjectByType<AssemblyBench>(FindObjectsInactive.Include);
        if (bench == null) { Debug.LogError("[Fix] AssemblyBench not found"); return; }

        // Activate the GameObject if it's inactive
        if (!bench.gameObject.activeSelf)
        {
            bench.gameObject.SetActive(true);
            Debug.Log("[Fix] AssemblyBench was inactive — activated.");
        }

        var palletGO = GameObject.Find("AssemblyPallet");
        if (palletGO == null) { Debug.LogError("[Fix] AssemblyPallet not found"); return; }

        var ghostMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/utility/M_shdr_alpha.mat");
        if (ghostMat == null) { Debug.LogError("[Fix] M_shdr_alpha.mat not found"); return; }

        var fPallet   = typeof(AssemblyBench).GetField("_assemblyPallet", BindingFlags.NonPublic | BindingFlags.Instance);
        var fGhostAct = typeof(AssemblyBench).GetField("_ghostActiveMat", BindingFlags.NonPublic | BindingFlags.Instance);

        fPallet?.SetValue(bench, palletGO.transform);
        fGhostAct?.SetValue(bench, ghostMat);

        EditorUtility.SetDirty(bench);
        EditorSceneManager.MarkSceneDirty(bench.gameObject.scene);
        EditorSceneManager.SaveScene(bench.gameObject.scene);

        var pallet = fPallet?.GetValue(bench) as Transform;
        var mat    = fGhostAct?.GetValue(bench) as Material;
        Debug.Log($"[Fix] _assemblyPallet = {pallet?.name} at {pallet?.position}");
        Debug.Log($"[Fix] _ghostActiveMat = {mat?.name}");
        Debug.Log("[Fix] Scene saved.");
    }
}
