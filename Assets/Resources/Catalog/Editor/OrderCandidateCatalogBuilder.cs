#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the extra regular-order catalog from prefabs that already ship with the project.
/// Copying a known-good part/order pair keeps the generated assets compatible with the
/// current OrderDefinition and PartDefinition serialization layout.
/// </summary>
[InitializeOnLoad]
internal static class OrderCandidateCatalogBuilder
{
    private const string PartTemplate = "Assets/Resources/Catalog/Parts/part_lawn_mower.asset";
    private const string OrderTemplate = "Assets/Resources/Catalog/Orders/order_lawn_mower.asset";
    private const string PartFolder = "Assets/Resources/Catalog/Parts";
    private const string OrderFolder = "Assets/Resources/Catalog/Orders";

    private static readonly Candidate[] Candidates =
    {
        new("hand_scanner", "Hand Scanner", "LogiScan", "A handheld warehouse scanner.", 110, false,
            "Scanner_02a_Prefab_01", "CyberneticWalrus"),
        new("beer_keg", "Beer Keg", "Dockside Brewery", "A sealed industrial beer keg.", 150, true,
            "Beer_Keg_01a_Prefab_01", "CyberneticWalrus"),
        new("cable_drum", "Cable Drum", "VoltLine", "A heavy drum of industrial cable.", 190, true,
            "Cable_Drum_01a_Prefab_01", "CyberneticWalrus"),
        new("industrial_scale", "Industrial Scale", "WeighTech", "A calibrated industrial floor scale.", 220, true,
            "Industrial_Scale_01a_Prefab_01", "CyberneticWalrus"),
        new("tool_set", "Tool Set", "Workshop Supply", "A complete warehouse maintenance tool set.", 125, false,
            "set_of_tools", "Assets/Prefabs/env"),
        new("warehouse_tablet", "Warehouse Tablet", "FieldOps", "A rugged tablet for warehouse operations.", 135, false,
            "tablet_prefab", "Assets/Prefabs/env"),
        new("warehouse_cart", "Warehouse Cart", "CargoFlow", "A heavy-duty warehouse transport cart.", 240, true,
            "Cart_01a_Prefab_01", "CyberneticWalrus"),
        new("pallet_truck", "Pallet Truck", "LiftWorks", "A manual hydraulic pallet truck.", 320, true,
            "Pallet_Truck_01a_Prefab_01", "CyberneticWalrus"),
        new("wrapping_machine", "Wrapping Machine", "PackRight", "An industrial pallet wrapping machine.", 450, true,
            "Wrapping_Machine_01a_Prefab_01", "CyberneticWalrus"),
        new("scissor_lift", "Scissor Lift", "HighReach Rentals", "A mobile industrial scissor lift.", 600, true,
            "scissor_lift", "Assets/Prefabs/env"),
        new("reach_truck", "Reach Truck", "Warehouse Mobility", "A warehouse reach truck for high-rack operations.", 900, true,
            "Reach_Truck", "CyberneticWalrus")
    };

    private static bool queued;

    static OrderCandidateCatalogBuilder()
    {
        QueueAutomaticBuild();
    }

    [MenuItem("Tools/Shift Happens Together/Build Extra Regular Orders")]
    private static void BuildFromMenu()
    {
        BuildCatalog(false);
    }

    private static void QueueAutomaticBuild()
    {
        if (queued)
            return;

        queued = true;
        EditorApplication.delayCall += () =>
        {
            queued = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                return;

            BuildCatalog(true);
        };
    }

    private static void BuildCatalog(bool onlyMissing)
    {
        if (AssetDatabase.LoadMainAssetAtPath(PartTemplate) == null ||
            AssetDatabase.LoadMainAssetAtPath(OrderTemplate) == null)
        {
            Debug.LogError("[Order Catalog] Template part or order asset is missing.");
            return;
        }

        EnsureFolder(PartFolder);
        EnsureFolder(OrderFolder);

        int created = 0;
        int updated = 0;
        var failures = new List<string>();

        try
        {
            foreach (Candidate candidate in Candidates)
            {
                string partPath = $"{PartFolder}/part_{candidate.Id}.asset";
                string orderPath = $"{OrderFolder}/order_{candidate.Id}.asset";

                if (onlyMissing && AssetDatabase.LoadMainAssetAtPath(partPath) != null &&
                    AssetDatabase.LoadMainAssetAtPath(orderPath) != null)
                    continue;

                GameObject prefab = FindPrefab(candidate.PrefabSearch, candidate.PreferredPath);
                if (prefab == null)
                {
                    failures.Add($"{candidate.DisplayName}: prefab '{candidate.PrefabSearch}' was not found");
                    continue;
                }

                bool partWasCreated = EnsureCopy(PartTemplate, partPath);
                bool orderWasCreated = EnsureCopy(OrderTemplate, orderPath);
                UnityEngine.Object part = AssetDatabase.LoadMainAssetAtPath(partPath);
                UnityEngine.Object order = AssetDatabase.LoadMainAssetAtPath(orderPath);

                if (part == null || order == null)
                {
                    failures.Add($"{candidate.DisplayName}: failed to create catalog assets");
                    continue;
                }

                ConfigurePart(part, prefab, candidate);
                ConfigureOrder(order, part, prefab, candidate);
                created += (partWasCreated ? 1 : 0) + (orderWasCreated ? 1 : 0);
                updated += (partWasCreated ? 0 : 1) + (orderWasCreated ? 0 : 1);
            }
        }
        finally
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        if (failures.Count == 0)
        {
            Debug.Log($"[Order Catalog] Extra regular orders are ready. Created {created} assets, updated {updated} assets.");
        }
        else
        {
            Debug.LogError("[Order Catalog] Catalog completed with missing products:\n- " +
                           string.Join("\n- ", failures));
        }
    }

    private static void ConfigurePart(UnityEngine.Object asset, GameObject prefab, Candidate candidate)
    {
        var serialized = new SerializedObject(asset);
        SetString(serialized, candidate.Id, "Id", "PartId", "ID");
        SetString(serialized, candidate.DisplayName, "DisplayName", "PartName", "Name");
        SetString(serialized, candidate.Description, "Description");
        SetObject(serialized, prefab, "VisualPrefab", "Prefab", "PartPrefab", "WorldPrefab");
        SetBool(serialized, candidate.IsHeavy, "IsHeavy", "Heavy");
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
    }

    private static void ConfigureOrder(UnityEngine.Object asset, UnityEngine.Object part, GameObject prefab,
        Candidate candidate)
    {
        var serialized = new SerializedObject(asset);
        SetString(serialized, candidate.Id, "Id", "OrderId", "ID");
        SetString(serialized, candidate.DisplayName, "DisplayName", "OrderName", "ProductName", "Name");
        SetString(serialized, candidate.ClientName, "ClientName", "CustomerName", "Client");
        SetString(serialized, candidate.Description, "Description");
        SetInt(serialized, candidate.Reward, "Reward", "BaseReward", "Payment", "Price");
        SetBool(serialized, candidate.IsHeavy, "IsHeavy", "Heavy");
        SetObject(serialized, prefab, "SharedGhostPrefab", "GhostPrefab", "VisualPrefab", "ProductPrefab");

        SerializedProperty requirements = serialized.FindProperty("RequiredParts");
        if ((requirements == null || requirements.arraySize <= 1) && !SetPartRequirement(serialized, part))
            Debug.LogError($"[Order Catalog] Could not assign required part for {candidate.DisplayName}.", asset);

        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
    }

    private static bool SetPartRequirement(SerializedObject serialized, UnityEngine.Object part)
    {
        SerializedProperty iterator = serialized.GetIterator();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (!iterator.isArray || iterator.propertyType == SerializedPropertyType.String ||
                !Normalize(iterator.name).Contains("part"))
                continue;

            iterator.arraySize = 1;
            SerializedProperty element = iterator.GetArrayElementAtIndex(0);
            if (element.propertyType == SerializedPropertyType.ObjectReference)
            {
                element.objectReferenceValue = part;
                return true;
            }

            string root = element.propertyPath;
            SerializedProperty child = element.Copy();
            bool assigned = false;
            bool visitChildren = true;
            while (child.NextVisible(visitChildren) && child.propertyPath.StartsWith(root, StringComparison.Ordinal))
            {
                visitChildren = false;
                string childName = Normalize(child.name);
                if (child.propertyType == SerializedPropertyType.ObjectReference && childName.Contains("part"))
                {
                    child.objectReferenceValue = part;
                    assigned = true;
                }
                else if (child.propertyType == SerializedPropertyType.Integer &&
                         (childName.Contains("quantity") || childName.Contains("count") || childName.Contains("amount")))
                {
                    child.intValue = 1;
                }
            }

            if (assigned)
                return true;
        }

        return false;
    }

    private static GameObject FindPrefab(string search, string preferredPath)
    {
        string[] guids = AssetDatabase.FindAssets($"{search} t:Prefab");
        GameObject fallback = null;
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
                continue;

            fallback ??= prefab;
            string fileName = Path.GetFileNameWithoutExtension(path);
            if (fileName.Equals(search, StringComparison.OrdinalIgnoreCase) &&
                path.IndexOf(preferredPath, StringComparison.OrdinalIgnoreCase) >= 0)
                return prefab;
        }

        return fallback;
    }

    private static bool EnsureCopy(string source, string destination)
    {
        if (AssetDatabase.LoadMainAssetAtPath(destination) != null)
            return false;

        if (!AssetDatabase.CopyAsset(source, destination))
            throw new InvalidOperationException($"Failed to copy '{source}' to '{destination}'.");

        AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
        return true;
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder))
            return;

        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        string name = Path.GetFileName(folder);
        if (!string.IsNullOrEmpty(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }

    private static bool SetString(SerializedObject target, string value, params string[] names)
    {
        SerializedProperty property = Find(target, SerializedPropertyType.String, names);
        if (property == null)
            return false;
        property.stringValue = value;
        return true;
    }

    private static bool SetInt(SerializedObject target, int value, params string[] names)
    {
        SerializedProperty property = Find(target, SerializedPropertyType.Integer, names);
        if (property == null)
            return false;
        property.intValue = value;
        return true;
    }

    private static bool SetBool(SerializedObject target, bool value, params string[] names)
    {
        SerializedProperty property = Find(target, SerializedPropertyType.Boolean, names);
        if (property == null)
            return false;
        property.boolValue = value;
        return true;
    }

    private static bool SetObject(SerializedObject target, UnityEngine.Object value, params string[] names)
    {
        SerializedProperty property = Find(target, SerializedPropertyType.ObjectReference, names);
        if (property == null)
            return false;
        property.objectReferenceValue = value;
        return true;
    }

    private static SerializedProperty Find(SerializedObject target, SerializedPropertyType expectedType,
        params string[] names)
    {
        foreach (string name in names)
        {
            SerializedProperty direct = target.FindProperty(name);
            if (direct != null && direct.propertyType == expectedType)
                return direct;
        }

        var normalizedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
            normalizedNames.Add(Normalize(name));

        SerializedProperty iterator = target.GetIterator();
        bool enterChildren = true;
        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (iterator.depth == 0 && iterator.propertyType == expectedType &&
                normalizedNames.Contains(Normalize(iterator.name)))
                return iterator.Copy();
        }

        return null;
    }

    private static string Normalize(string value)
    {
        return value.Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
    }

    private readonly struct Candidate
    {
        public Candidate(string id, string displayName, string clientName, string description, int reward,
            bool isHeavy, string prefabSearch, string preferredPath)
        {
            Id = id;
            DisplayName = displayName;
            ClientName = clientName;
            Description = description;
            Reward = reward;
            IsHeavy = isHeavy;
            PrefabSearch = prefabSearch;
            PreferredPath = preferredPath;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string ClientName { get; }
        public string Description { get; }
        public int Reward { get; }
        public bool IsHeavy { get; }
        public string PrefabSearch { get; }
        public string PreferredPath { get; }
    }
}
#endif
