using UnityEngine;
using UnityEditor;

public class PrefabInspector
{
    const string ENV = "Assets/Prefabs/env/";

    public static void Execute()
    {
        Check("wall_plain_GRP_prefab");
    }

    public static void CheckFloor()  { Check("floor_TYPE_A_01_prefab"); }
    public static void CheckCeil()   { Check("ceiling_realtime_01_prefab"); }
    public static void CheckPoles()  { Check("poles_GRP_prefab"); }
    public static void CheckShelf()  { Check("shelfLarge_prefab"); }
    public static void CheckLight()  { Check("koneLight_prefab"); }
    public static void CheckEnvDay() { Check("env_UserDay_prefab"); }

    static void Check(string name)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ENV + name + ".prefab");
        if (prefab == null) { Debug.Log($"[PI] {name}: NOT FOUND"); return; }

        var renderers = prefab.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) { Debug.Log($"[PI] {name}: no renderers"); return; }

        // instantiate temporarily to get correct world bounds
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        go.transform.position = Vector3.zero;
        go.transform.rotation = Quaternion.identity;

        Bounds b = new Bounds(Vector3.zero, Vector3.zero);
        bool first = true;
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (first) { b = r.bounds; first = false; }
            else b.Encapsulate(r.bounds);
        }

        Object.DestroyImmediate(go);
        Debug.Log($"[PI] {name}: size={b.size} center={b.center}");
    }
}
