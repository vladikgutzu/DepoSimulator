using UnityEngine;
using UnityEditor;
using Unity.Netcode;

// Tools > Warehouse Demo > Setup Vehicle Delivery
//
// Creates (or updates) the VehicleDeliveryManager scene object, gives it a NetworkObject
// (required since it's a NetworkBehaviour used by the multiplayer ServerRpc/ClientRpc path),
// and wires the default box/pallet prefabs. Safe to re-run.
public static class SetupVehicleDelivery
{
    const string ManagerName = "VehicleDeliveryManager";
    const string BoxPrefabPath    = "Assets/Prefabs/env/box_med_prefab.prefab";
    const string PalletPrefabPath = "Assets/Prefabs/env/pallet_prefab.prefab";

    [MenuItem("Tools/Warehouse Demo/Setup Vehicle Delivery")]
    public static void Execute()
    {
        var go = GameObject.Find(ManagerName);
        if (go == null)
        {
            go = new GameObject(ManagerName);
            Undo.RegisterCreatedObjectUndo(go, "Create VehicleDeliveryManager");
        }

        if (go.GetComponent<NetworkObject>() == null)
            Undo.AddComponent<NetworkObject>(go);

        var mgr = go.GetComponent<VehicleDeliveryManager>();
        if (mgr == null)
            mgr = Undo.AddComponent<VehicleDeliveryManager>(go);

        var so = new SerializedObject(mgr);

        AssignIfEmpty(so, "_boxPrefab", BoxPrefabPath);
        AssignIfEmpty(so, "_palletPrefab", PalletPrefabPath);

        so.ApplyModifiedProperties();

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

        Debug.Log("[VehicleDeliveryManager] Scene object ready. Assign a VehiclePartKit to any "
                + "NetworkShopManager.ShopEntry to route that vehicle through the delivery flow. "
                + "Remember to save the scene (Ctrl+S).");
    }

    static void AssignIfEmpty(SerializedObject so, string field, string assetPath)
    {
        var prop = so.FindProperty(field);
        if (prop == null || prop.objectReferenceValue != null) return;

        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (asset == null)
        {
            Debug.LogWarning($"[VehicleDeliveryManager] Prefab not found: {assetPath}");
            return;
        }
        prop.objectReferenceValue = asset;
    }
}
