using UnityEngine;
using UnityEditor;

// Tools > Warehouse Demo > Setup Vehicle Part Kits
//
// One-time content authoring: creates a single-part VehiclePartKit per shop vehicle (all three
// current vehicle FBXs — Cart, Pallet Jack, Forklift — are a single mesh, confirmed by checking
// MeshRenderer count in the scene) and assigns it to the matching NetworkShopManager.ShopEntry.
// Safe to re-run: skips creation when the asset/assignment already exists.
public static class SetupVehiclePartKits
{
    const string PartFolder = "Assets/Resources/VehicleParts";
    const string KitFolder  = "Assets/Data/VehicleKits";

    struct VehicleSpec
    {
        public NetworkShopManager.VehicleType Type;
        public string PartId;
        public string PartName;
        public Color  DebugColor;
        public float  DeliveryDelay;
    }

    static readonly VehicleSpec[] s_specs =
    {
        new VehicleSpec { Type = NetworkShopManager.VehicleType.Cart,       PartId = "cart_body",        PartName = "Cart Body",        DebugColor = new Color(0.85f, 0.65f, 0.2f), DeliveryDelay = 15f },
        new VehicleSpec { Type = NetworkShopManager.VehicleType.PalletJack, PartId = "pallet_jack_body",  PartName = "Pallet Jack Body",  DebugColor = new Color(0.3f,  0.55f, 0.85f), DeliveryDelay = 25f },
        new VehicleSpec { Type = NetworkShopManager.VehicleType.Forklift,   PartId = "forklift_body",     PartName = "Forklift Body",     DebugColor = new Color(0.85f, 0.25f, 0.2f), DeliveryDelay = 40f },
    };

    public static void Execute()
    {
        EnsureFolder(PartFolder);
        EnsureFolder(KitFolder);

        var go  = GameObject.Find("NetworkShopManager");
        var mgr = go != null ? go.GetComponent<NetworkShopManager>() : null;
        if (mgr == null) { Debug.LogError("[SetupVehiclePartKits] NetworkShopManager not found in scene."); return; }

        var so  = new SerializedObject(mgr);
        var arr = so.FindProperty("_entries");

        foreach (var spec in s_specs)
        {
            var part = GetOrCreatePart(spec);
            var kit  = GetOrCreateKit(spec, part);
            AssignKitToEntry(arr, spec.Type, kit);
        }

        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
        Debug.Log("[SetupVehiclePartKits] Done — Cart/PalletJack/Forklift now route through VehicleDeliveryManager.");
    }

    static PartDefinition GetOrCreatePart(VehicleSpec spec)
    {
        string path = $"{PartFolder}/part_{spec.PartId}.asset";
        var existing = AssetDatabase.LoadAssetAtPath<PartDefinition>(path);
        if (existing != null) return existing;

        var part = ScriptableObject.CreateInstance<PartDefinition>();
        part.PartName    = spec.PartName;
        part.PartID      = spec.PartId;
        part.Description = $"Delivery part: {spec.PartName} (spawns as a single box — the vehicle FBX is one mesh).";
        part.DebugColor  = spec.DebugColor;
        part.IsHeavy     = true;

        AssetDatabase.CreateAsset(part, path);
        Debug.Log($"[SetupVehiclePartKits] Created {path}");
        return part;
    }

    static VehiclePartKit GetOrCreateKit(VehicleSpec spec, PartDefinition part)
    {
        string path = $"{KitFolder}/kit_{spec.PartId}.asset";
        var existing = AssetDatabase.LoadAssetAtPath<VehiclePartKit>(path);
        if (existing != null)
        {
            existing.Parts = new[] { part };
            existing.DeliveryDelaySeconds = spec.DeliveryDelay;
            EditorUtility.SetDirty(existing);
            return existing;
        }

        var kit = ScriptableObject.CreateInstance<VehiclePartKit>();
        kit.Parts = new[] { part };
        kit.DeliveryDelaySeconds = spec.DeliveryDelay;

        AssetDatabase.CreateAsset(kit, path);
        Debug.Log($"[SetupVehiclePartKits] Created {path}");
        return kit;
    }

    static void AssignKitToEntry(SerializedProperty arr, NetworkShopManager.VehicleType type, VehiclePartKit kit)
    {
        for (int i = 0; i < arr.arraySize; i++)
        {
            var el = arr.GetArrayElementAtIndex(i);
            var typeProp = el.FindPropertyRelative("Type");
            if ((NetworkShopManager.VehicleType)typeProp.enumValueIndex != type) continue;

            el.FindPropertyRelative("PartKit").objectReferenceValue = kit;
            Debug.Log($"[SetupVehiclePartKits] Assigned {kit.name} to ShopEntry[{i}] ({type}).");
            return;
        }
        Debug.LogWarning($"[SetupVehiclePartKits] No ShopEntry found for {type}.");
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace("\\", "/");
        string leaf   = System.IO.Path.GetFileName(path);
        if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
