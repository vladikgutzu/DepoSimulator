#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Converts the extra regular-order catalog into staged, multi-part assemblies.
/// The source product models are intentionally left untouched: many are single imported meshes,
/// so compact physical component prefabs are generated for the pickup boxes instead.
/// </summary>
[InitializeOnLoad]
internal static class MultiPartOrderCatalogBuilder
{
    private const string PartsFolder = "Assets/Resources/Catalog/Parts";
    private const string OrdersFolder = "Assets/Resources/Catalog/Orders";
    private const string VisualsFolder = "Assets/Resources/Catalog/PartVisuals";
    private const string MaterialsFolder = VisualsFolder + "/Materials";

    private static readonly OrderSpec[] Orders =
    {
        new("hand_scanner",
            P("hand_scanner_housing", "Scanner Housing", "Rugged scanner body and electronics.", Shape.Box, C(0.16f, 0.48f, 0.75f), false, 1, 0),
            P("hand_scanner_battery", "Scanner Battery", "Rechargeable scanner battery pack.", Shape.Battery, C(0.95f, 0.62f, 0.15f), false, 1, 1),
            P("hand_scanner_dock", "Scanner Charging Dock", "Charging and synchronization dock.", Shape.Dock, C(0.15f, 0.62f, 0.90f), false, 1, 2, true)),

        new("beer_keg",
            P("beer_keg_shell", "Keg Shell", "Pressure-rated steel keg body.", Shape.Cylinder, C(0.48f, 0.52f, 0.56f), true, 1, 0),
            P("beer_keg_valve", "Keg Valve", "Top dispensing valve assembly.", Shape.Hydraulic, C(0.82f, 0.70f, 0.18f), false, 1, 1),
            P("beer_keg_cap", "Keg Protective Cap", "Protective cap for the keg valve.", Shape.Cap, C(0.92f, 0.82f, 0.18f), false, 1, 2, true)),

        new("cable_drum",
            P("cable_drum_core", "Cable Drum Core", "Load-bearing drum core.", Shape.Cylinder, C(0.46f, 0.30f, 0.16f), true, 1, 0),
            P("cable_drum_spool", "Industrial Cable Spool", "Coiled industrial power cable.", Shape.Spool, C(0.10f, 0.12f, 0.14f), true, 1, 1),
            P("cable_drum_side_plate", "Drum Side Plate", "Reinforced side plate for the cable drum.", Shape.Plate, C(0.74f, 0.47f, 0.20f), false, 2, 2, true)),

        new("industrial_scale",
            P("industrial_scale_platform", "Scale Platform", "Reinforced weighing platform.", Shape.Platform, C(0.25f, 0.34f, 0.38f), true, 1, 0),
            P("industrial_scale_stand", "Scale Display Stand", "Support column for the readout.", Shape.Column, C(0.28f, 0.38f, 0.46f), false, 1, 1),
            P("industrial_scale_display", "Scale Display", "Calibrated digital weight display.", Shape.Display, C(0.15f, 0.62f, 0.82f), false, 1, 2, true)),

        new("tool_set",
            P("tool_set_case", "Tool Case", "Heavy-duty protective tool case.", Shape.Box, C(0.16f, 0.20f, 0.24f), false, 1, 0),
            P("tool_set_tools", "Maintenance Tools", "Organized warehouse maintenance tools.", Shape.ToolKit, C(0.82f, 0.35f, 0.12f), false, 1, 1),
            P("tool_set_battery", "Tool Battery", "Rechargeable battery for powered tools.", Shape.Battery, C(0.95f, 0.55f, 0.10f), false, 1, 2, true)),

        new("warehouse_tablet",
            P("warehouse_tablet_body", "Tablet Body", "Rugged warehouse tablet computer.", Shape.Display, C(0.12f, 0.16f, 0.20f), false, 1, 0),
            P("warehouse_tablet_case", "Tablet Protective Case", "Impact-resistant tablet frame.", Shape.Frame, C(0.92f, 0.58f, 0.12f), false, 1, 1),
            P("warehouse_tablet_charger", "Tablet Charger", "Charging base and power connector.", Shape.Dock, C(0.15f, 0.62f, 0.90f), false, 1, 2, true)),

        new("warehouse_cart",
            P("warehouse_cart_platform", "Cart Platform", "Reinforced cart load platform.", Shape.Platform, C(0.20f, 0.28f, 0.34f), true, 1, 0),
            P("warehouse_cart_handle", "Cart Handle", "Steel steering handle.", Shape.Handle, C(0.80f, 0.32f, 0.14f), false, 1, 1),
            P("warehouse_cart_wheel", "Cart Wheel", "Heavy-duty swivel wheel.", Shape.Wheel, C(0.12f, 0.12f, 0.13f), false, 4, 2, true)),

        new("pallet_truck",
            P("pallet_truck_chassis", "Pallet Truck Chassis", "Forked load-bearing chassis.", Shape.Forks, C(0.85f, 0.55f, 0.12f), true, 1, 0),
            P("pallet_truck_handle", "Pallet Truck Handle", "Steering and pump handle.", Shape.Handle, C(0.82f, 0.32f, 0.12f), false, 1, 1),
            P("pallet_truck_hydraulic", "Hydraulic Pump", "Hydraulic lifting unit.", Shape.Hydraulic, C(0.92f, 0.62f, 0.12f), true, 1, 1),
            P("pallet_truck_roller", "Load Roller", "High-load fork roller.", Shape.Roller, C(0.12f, 0.12f, 0.13f), false, 2, 2, true)),

        new("wrapping_machine",
            P("wrapping_machine_base", "Wrapper Base", "Rotating pallet wrapper base.", Shape.Platform, C(0.24f, 0.30f, 0.34f), true, 1, 0),
            P("wrapping_machine_column", "Wrapper Column", "Vertical carriage support column.", Shape.Column, C(0.28f, 0.34f, 0.42f), true, 1, 1),
            P("wrapping_machine_carriage", "Film Carriage", "Stretch-film dispensing carriage.", Shape.Carriage, C(0.92f, 0.70f, 0.16f), false, 1, 2, true)),

        new("scissor_lift",
            P("scissor_lift_base", "Scissor Lift Base", "Mobile powered lift chassis.", Shape.Platform, C(0.22f, 0.28f, 0.34f), true, 1, 0),
            P("scissor_lift_mechanism", "Scissor Mechanism", "Cross-linked hydraulic lift mechanism.", Shape.Scissor, C(0.90f, 0.66f, 0.14f), true, 1, 1),
            P("scissor_lift_platform", "Lift Platform", "Guarded operator platform.", Shape.Frame, C(0.92f, 0.76f, 0.18f), true, 1, 2, true)),

        new("reach_truck",
            P("reach_truck_chassis", "Reach Truck Chassis", "Powered reach-truck chassis.", Shape.Platform, C(0.16f, 0.20f, 0.24f), true, 1, 0),
            P("reach_truck_mast", "Reach Truck Mast", "Telescoping high-rack mast.", Shape.Mast, C(0.18f, 0.24f, 0.30f), true, 1, 1),
            P("reach_truck_forks", "Reach Truck Forks", "Adjustable pallet forks.", Shape.Forks, C(0.82f, 0.55f, 0.12f), true, 1, 1),
            P("reach_truck_battery", "Reach Truck Battery", "Industrial traction battery.", Shape.Battery, C(0.95f, 0.58f, 0.12f), true, 1, 2, true))
    };

    static MultiPartOrderCatalogBuilder()
    {
        EditorApplication.delayCall += () =>
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
                Build(false);
        };
    }

    [MenuItem("Tools/Shift Happens Together/Build Multi-Part Regular Orders")]
    private static void BuildFromMenu() => Build(true);

    private static void Build(bool force)
    {
        if (!force && Orders.All(IsConfigured))
            return;

        EnsureFolder(VisualsFolder);
        EnsureFolder(MaterialsFolder);

        int partCount = 0;
        foreach (OrderSpec spec in Orders)
        {
            string orderPath = $"{OrdersFolder}/order_{spec.Id}.asset";
            OrderDefinition order = AssetDatabase.LoadAssetAtPath<OrderDefinition>(orderPath);
            if (order == null)
            {
                Debug.LogError($"[Multi-Part Catalog] Missing order asset: {orderPath}");
                continue;
            }

            var requirements = new List<OrderDefinition.PartRequirement>();
            for (int i = 0; i < spec.Parts.Length; i++)
            {
                PartSpec partSpec = spec.Parts[i];
                string partPath = i == 0
                    ? $"{PartsFolder}/part_{spec.Id}.asset"
                    : $"{PartsFolder}/part_{partSpec.Id}.asset";

                GameObject visual = BuildVisual(partSpec, force);
                PartDefinition part = AssetDatabase.LoadAssetAtPath<PartDefinition>(partPath);
                if (part == null)
                {
                    part = ScriptableObject.CreateInstance<PartDefinition>();
                    AssetDatabase.CreateAsset(part, partPath);
                }

                part.PartName = partSpec.Name;
                part.PartID = partSpec.Id;
                part.Description = partSpec.Description;
                part.VisualPrefab = visual;
                part.GhostPrefab = null;
                part.DebugColor = partSpec.Color;
                part.IsHeavy = partSpec.IsHeavy;
                part.GhostChildPaths = new[] { partSpec.RevealProduct ? "$all" : "$none" };
                EditorUtility.SetDirty(part);

                requirements.Add(new OrderDefinition.PartRequirement
                {
                    Part = part,
                    Count = partSpec.Count,
                    AssemblyStage = partSpec.Stage
                });
                partCount += partSpec.Count;
            }

            order.RequiredParts = requirements.ToArray();
            EditorUtility.SetDirty(order);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[Multi-Part Catalog] Configured {Orders.Length} orders and {partCount} physical components.");
    }

    private static bool IsConfigured(OrderSpec spec)
    {
        OrderDefinition order = AssetDatabase.LoadAssetAtPath<OrderDefinition>($"{OrdersFolder}/order_{spec.Id}.asset");
        if (order == null || order.RequiredParts == null || order.RequiredParts.Length != spec.Parts.Length)
            return false;

        for (int i = 0; i < spec.Parts.Length; i++)
        {
            var req = order.RequiredParts[i];
            if (req.Part == null || req.Part.PartID != spec.Parts[i].Id ||
                req.Count != spec.Parts[i].Count || req.AssemblyStage != spec.Parts[i].Stage)
                return false;
        }
        return true;
    }

    private static GameObject BuildVisual(PartSpec spec, bool force)
    {
        string path = $"{VisualsFolder}/{spec.Id}.prefab";
        GameObject existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null && !force)
            return existing;

        Material material = GetMaterial(spec.Color);
        var root = new GameObject(spec.Id);
        try
        {
            switch (spec.VisualShape)
            {
                case Shape.Box: Box(root.transform, "Housing", new Vector3(.42f, .24f, .32f), new Vector3(0f, .12f, 0f), material); break;
                case Shape.Battery: BuildBattery(root.transform, material); break;
                case Shape.Dock: BuildDock(root.transform, material); break;
                case Shape.Cylinder: Cylinder(root.transform, "Shell", .22f, .48f, Vector3.zero, Quaternion.identity, material); break;
                case Shape.Hydraulic: BuildHydraulic(root.transform, material); break;
                case Shape.Cap: Cylinder(root.transform, "Cap", .18f, .10f, Vector3.zero, Quaternion.identity, material); break;
                case Shape.Spool: BuildSpool(root.transform, material); break;
                case Shape.Plate: Cylinder(root.transform, "SidePlate", .26f, .06f, Vector3.zero, Quaternion.Euler(0f, 0f, 90f), material); break;
                case Shape.Platform: BuildPlatform(root.transform, material); break;
                case Shape.Column: BuildColumn(root.transform, material); break;
                case Shape.Display: BuildDisplay(root.transform, material); break;
                case Shape.ToolKit: BuildToolKit(root.transform, material); break;
                case Shape.Frame: BuildFrame(root.transform, material); break;
                case Shape.Handle: BuildHandle(root.transform, material); break;
                case Shape.Wheel: Cylinder(root.transform, "Wheel", .16f, .10f, Vector3.zero, Quaternion.Euler(0f, 0f, 90f), material); break;
                case Shape.Forks: BuildForks(root.transform, material); break;
                case Shape.Roller: Cylinder(root.transform, "Roller", .12f, .22f, Vector3.zero, Quaternion.Euler(0f, 0f, 90f), material); break;
                case Shape.Carriage: BuildCarriage(root.transform, material); break;
                case Shape.Scissor: BuildScissor(root.transform, material); break;
                case Shape.Mast: BuildMast(root.transform, material); break;
                default: Box(root.transform, "Part", new Vector3(.30f, .20f, .30f), new Vector3(0f, .10f, 0f), material); break;
            }

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            return prefab;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static Material GetMaterial(Color color)
    {
        string key = ColorUtility.ToHtmlStringRGB(color).ToLowerInvariant();
        string path = $"{MaterialsFolder}/part_{key}.mat";
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        material = new Material(shader) { name = $"Part {key}" };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        material.SetFloat("_Smoothness", .28f);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    private static void BuildBattery(Transform root, Material mat)
    {
        Box(root, "Battery", new Vector3(.34f, .22f, .28f), new Vector3(0f, .11f, 0f), mat);
        Cylinder(root, "TerminalA", .035f, .06f, new Vector3(-.09f, .25f, 0f), Quaternion.identity, mat);
        Cylinder(root, "TerminalB", .035f, .06f, new Vector3(.09f, .25f, 0f), Quaternion.identity, mat);
    }

    private static void BuildDock(Transform root, Material mat)
    {
        Box(root, "Base", new Vector3(.42f, .08f, .32f), new Vector3(0f, .04f, 0f), mat);
        Box(root, "Back", new Vector3(.36f, .30f, .07f), new Vector3(0f, .19f, .12f), mat);
    }

    private static void BuildHydraulic(Transform root, Material mat)
    {
        Cylinder(root, "Cylinder", .10f, .38f, new Vector3(0f, .19f, 0f), Quaternion.identity, mat);
        Cylinder(root, "Rod", .045f, .20f, new Vector3(0f, .48f, 0f), Quaternion.identity, mat);
    }

    private static void BuildSpool(Transform root, Material mat)
    {
        Cylinder(root, "Cable", .19f, .30f, Vector3.zero, Quaternion.Euler(0f, 0f, 90f), mat);
        Cylinder(root, "Hub", .07f, .40f, Vector3.zero, Quaternion.Euler(0f, 0f, 90f), mat);
    }

    private static void BuildPlatform(Transform root, Material mat)
    {
        Box(root, "Platform", new Vector3(.55f, .10f, .42f), new Vector3(0f, .05f, 0f), mat);
        Box(root, "Brace", new Vector3(.40f, .12f, .30f), new Vector3(0f, .16f, 0f), mat);
    }

    private static void BuildColumn(Transform root, Material mat)
    {
        Box(root, "Foot", new Vector3(.34f, .08f, .30f), new Vector3(0f, .04f, 0f), mat);
        Box(root, "Column", new Vector3(.12f, .58f, .12f), new Vector3(0f, .33f, 0f), mat);
    }

    private static void BuildDisplay(Transform root, Material mat)
    {
        Box(root, "DisplayBody", new Vector3(.40f, .28f, .07f), new Vector3(0f, .14f, 0f), mat);
        Box(root, "Screen", new Vector3(.29f, .16f, .02f), new Vector3(0f, .16f, -.045f), mat);
    }

    private static void BuildToolKit(Transform root, Material mat)
    {
        Box(root, "Tray", new Vector3(.48f, .05f, .32f), new Vector3(0f, .025f, 0f), mat);
        Box(root, "ToolA", new Vector3(.06f, .08f, .25f), new Vector3(-.14f, .09f, 0f), mat);
        Box(root, "ToolB", new Vector3(.06f, .08f, .25f), new Vector3(0f, .09f, 0f), mat);
        Box(root, "ToolC", new Vector3(.06f, .08f, .25f), new Vector3(.14f, .09f, 0f), mat);
    }

    private static void BuildFrame(Transform root, Material mat)
    {
        Box(root, "Bottom", new Vector3(.46f, .06f, .34f), new Vector3(0f, .03f, 0f), mat);
        Box(root, "RailL", new Vector3(.05f, .28f, .34f), new Vector3(-.205f, .17f, 0f), mat);
        Box(root, "RailR", new Vector3(.05f, .28f, .34f), new Vector3(.205f, .17f, 0f), mat);
    }

    private static void BuildHandle(Transform root, Material mat)
    {
        Box(root, "Grip", new Vector3(.44f, .07f, .07f), new Vector3(0f, .46f, 0f), mat);
        Box(root, "ArmL", new Vector3(.07f, .44f, .07f), new Vector3(-.185f, .24f, 0f), mat);
        Box(root, "ArmR", new Vector3(.07f, .44f, .07f), new Vector3(.185f, .24f, 0f), mat);
    }

    private static void BuildForks(Transform root, Material mat)
    {
        Box(root, "Crossbar", new Vector3(.48f, .18f, .16f), new Vector3(0f, .09f, .13f), mat);
        Box(root, "ForkL", new Vector3(.11f, .08f, .65f), new Vector3(-.16f, .04f, -.20f), mat);
        Box(root, "ForkR", new Vector3(.11f, .08f, .65f), new Vector3(.16f, .04f, -.20f), mat);
    }

    private static void BuildCarriage(Transform root, Material mat)
    {
        Box(root, "Carriage", new Vector3(.30f, .46f, .18f), new Vector3(0f, .23f, 0f), mat);
        Cylinder(root, "FilmRoll", .10f, .40f, new Vector3(.22f, .23f, 0f), Quaternion.identity, mat);
    }

    private static void BuildScissor(Transform root, Material mat)
    {
        Box(root, "ArmA", new Vector3(.08f, .58f, .08f), new Vector3(0f, .28f, 0f), Quaternion.Euler(0f, 0f, 38f), mat);
        Box(root, "ArmB", new Vector3(.08f, .58f, .08f), new Vector3(0f, .28f, 0f), Quaternion.Euler(0f, 0f, -38f), mat);
    }

    private static void BuildMast(Transform root, Material mat)
    {
        Box(root, "RailL", new Vector3(.09f, .70f, .10f), new Vector3(-.18f, .35f, 0f), mat);
        Box(root, "RailR", new Vector3(.09f, .70f, .10f), new Vector3(.18f, .35f, 0f), mat);
        Box(root, "Crossbar", new Vector3(.45f, .09f, .10f), new Vector3(0f, .62f, 0f), mat);
    }

    private static GameObject Box(Transform parent, string name, Vector3 size, Vector3 position, Material material)
        => Box(parent, name, size, position, Quaternion.identity, material);

    private static GameObject Box(Transform parent, string name, Vector3 size, Vector3 position,
        Quaternion rotation, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localRotation = rotation;
        go.transform.localScale = size;
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go;
    }

    private static GameObject Cylinder(Transform parent, string name, float radius, float height,
        Vector3 position, Quaternion rotation, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localRotation = rotation;
        go.transform.localScale = new Vector3(radius * 2f, height * .5f, radius * 2f);
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go;
    }

    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }

    private static PartSpec P(string id, string name, string description, Shape shape, Color color,
        bool heavy, int count, int stage, bool reveal = false)
        => new(id, name, description, shape, color, heavy, count, stage, reveal);

    private static Color C(float r, float g, float b) => new(r, g, b, 1f);

    private enum Shape { Box, Battery, Dock, Cylinder, Hydraulic, Cap, Spool, Plate, Platform, Column, Display, ToolKit, Frame, Handle, Wheel, Forks, Roller, Carriage, Scissor, Mast }

    private sealed class OrderSpec
    {
        public OrderSpec(string id, params PartSpec[] parts) { Id = id; Parts = parts; }
        public string Id { get; }
        public PartSpec[] Parts { get; }
    }

    private readonly struct PartSpec
    {
        public PartSpec(string id, string name, string description, Shape visualShape, Color color,
            bool isHeavy, int count, int stage, bool revealProduct)
        {
            Id = id; Name = name; Description = description; VisualShape = visualShape; Color = color;
            IsHeavy = isHeavy; Count = count; Stage = stage; RevealProduct = revealProduct;
        }
        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public Shape VisualShape { get; }
        public Color Color { get; }
        public bool IsHeavy { get; }
        public int Count { get; }
        public int Stage { get; }
        public bool RevealProduct { get; }
    }
}
#endif
