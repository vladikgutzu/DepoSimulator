using UnityEngine;
using UnityEditor;
using UnityEngine.SceneManagement;
using System.Reflection;

public static class SetupAllVehicles
{
    const string TruckName  = "Reach_Truck_01a_Rig_Prefab_01";
    const string Front1Path = "Reach_Truck_01a_Rig/Root/Body/Front_01";
    const string Front2Path = "Reach_Truck_01a_Rig/Root/Body/Front_01/Front_02";
    const string CartName   = "Cart_01a_Rig_Prefab_01";
    const string HandlePath = "Cart_01a_Rig/Root/Body/Handle";

    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Execute()
    {
        var log = new System.Text.StringBuilder();
        log.AppendLine("=== Setup All Vehicles ===");

        SetupReachTruck(log);
        SetupCart(log);
        CheckPlayer(log);

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            SceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(
            SceneManager.GetActiveScene());

        log.AppendLine("Scene saved.");
        Debug.Log(log.ToString());
    }

    // ── Reach Truck ───────────────────────────────────────────────────────────

    static void SetupReachTruck(System.Text.StringBuilder log)
    {
        var root = GameObject.Find(TruckName);
        if (root == null) { log.AppendLine($"[TRUCK] NOT FOUND: {TruckName}"); return; }

        // Add Collider first (required by ReachTruckController)
        if (root.GetComponent<Collider>() == null)
        {
            var bc = root.AddComponent<BoxCollider>();
            bc.isTrigger = false;
            var rends = root.GetComponentsInChildren<Renderer>(false);
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                var ls = root.transform.lossyScale;
                bc.center = root.transform.InverseTransformPoint(b.center);
                bc.size   = new Vector3(b.size.x / ls.x, b.size.y / ls.y, b.size.z / ls.z);
            }
            else bc.size = new Vector3(1.5f, 3f, 4f);
            log.AppendLine("[TRUCK] Added BoxCollider");
        }

        var ctrl = root.GetComponent<ReachTruckController>();
        if (ctrl == null)
        {
            ctrl = root.AddComponent<ReachTruckController>();
            log.AppendLine($"[TRUCK] AddComponent ReachTruckController → {(ctrl != null ? "OK" : "NULL")}");
        }
        else log.AppendLine("[TRUCK] ReachTruckController already exists");

        if (ctrl == null) { log.AppendLine("[TRUCK] ABORT - ctrl is null"); return; }

        var front1 = root.transform.Find(Front1Path);
        var front2 = root.transform.Find(Front2Path);
        log.AppendLine($"[TRUCK] Front_01={front1 != null}  Front_02={front2 != null}");

        Bounds bounds = ComputeBounds(root, new Vector3(2f, 3f, 4f));

        var seat = EnsureChild(root.transform, "DriverSeat",
            new Vector3(bounds.center.x, bounds.min.y + 0.5f, bounds.max.z - 0.4f),
            Quaternion.Euler(0f, 180f, 0f));
        var cam = EnsureChild(root.transform, "CameraMount",
            new Vector3(bounds.center.x, bounds.min.y + 2.0f, bounds.max.z - 1.2f),
            Quaternion.Euler(12f, 180f, 0f));
        var exit = EnsureChild(root.transform, "ExitPoint",
            new Vector3(bounds.max.x + 0.6f, 0f, bounds.max.z - 0.4f),
            Quaternion.Euler(0f, 90f, 0f));

        SetField(ctrl, "_front1",      front1);
        SetField(ctrl, "_front2",      front2);
        SetField(ctrl, "_driverSeat",  seat);
        SetField(ctrl, "_cameraMount", cam);
        SetField(ctrl, "_exitPoint",   exit);

        EditorUtility.SetDirty(root);
        log.AppendLine("[TRUCK] ✓ Done");
    }

    // ── Cart ──────────────────────────────────────────────────────────────────

    static void SetupCart(System.Text.StringBuilder log)
    {
        var root = GameObject.Find(CartName);
        if (root == null) { log.AppendLine($"[CART] NOT FOUND: {CartName}"); return; }

        // Add Collider first (required by CartController)
        if (root.GetComponent<Collider>() == null)
        {
            var bc = root.AddComponent<BoxCollider>();
            bc.isTrigger = false;
            var rends = root.GetComponentsInChildren<Renderer>(false);
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                var ls = root.transform.lossyScale;
                bc.center = root.transform.InverseTransformPoint(b.center);
                bc.size   = new Vector3(b.size.x / ls.x, b.size.y / ls.y, b.size.z / ls.z);
            }
            else bc.size = new Vector3(0.6f, 1.3f, 1f);
            log.AppendLine("[CART] Added BoxCollider");
        }

        var ctrl = root.GetComponent<CartController>();
        if (ctrl == null)
        {
            ctrl = root.AddComponent<CartController>();
            log.AppendLine($"[CART] AddComponent CartController → {(ctrl != null ? "OK" : "NULL")}");
        }
        else log.AppendLine("[CART] CartController already exists");

        if (ctrl == null) { log.AppendLine("[CART] ABORT - ctrl is null"); return; }

        var handle = root.transform.Find(HandlePath);
        log.AppendLine($"[CART] Handle={handle != null}");

        Bounds bounds = ComputeBounds(root, new Vector3(0.6f, 1.3f, 1f));

        var targetParent = handle != null ? handle : root.transform;
        float gripY   = bounds.min.y + bounds.size.y * 0.78f;
        float centerX = bounds.center.x;
        float handleZ = handle != null ? handle.position.z : bounds.min.z;

        var rightTarget = EnsureChild(targetParent, "HandTarget_R",
            new Vector3(centerX + 0.18f, gripY, handleZ), targetParent.rotation);
        var leftTarget  = EnsureChild(targetParent, "HandTarget_L",
            new Vector3(centerX - 0.18f, gripY, handleZ), targetParent.rotation);
        var playerAnchor = EnsureChild(root.transform, "PlayerAnchor",
            new Vector3(bounds.center.x, bounds.min.y + 0.02f, bounds.min.z - 0.35f),
            Quaternion.identity);
        var exitPoint = EnsureChild(root.transform, "ExitPoint",
            new Vector3(bounds.max.x + 0.55f, bounds.min.y + 0.02f, bounds.min.z),
            Quaternion.Euler(0f, 90f, 0f));

        SetField(ctrl, "_rightHandTarget", rightTarget);
        SetField(ctrl, "_leftHandTarget",  leftTarget);
        SetField(ctrl, "_playerAnchor",    playerAnchor);
        SetField(ctrl, "_exitPoint",       exitPoint);

        // CartStorageInteractable — lets players stack boxes on the cart platform.
        var storage = root.GetComponent<CartStorageInteractable>();
        if (storage == null)
        {
            storage = root.AddComponent<CartStorageInteractable>();
            log.AppendLine("[CART] Added CartStorageInteractable");
        }
        else log.AppendLine("[CART] CartStorageInteractable already exists");

        // StackBase — the pivot point on the cart platform where boxes are stacked.
        var stackBase = root.transform.Find("StackBase");
        if (stackBase == null)
        {
            var sb = new GameObject("StackBase");
            sb.transform.SetParent(root.transform);
            // Place on top of the cart platform (adjust Y in Inspector if needed).
            sb.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            sb.transform.localRotation = Quaternion.identity;
            stackBase = sb.transform;
            log.AppendLine("[CART] Created StackBase child");
        }
        SetField(storage, "_stackBase", stackBase);

        EditorUtility.SetDirty(root);
        log.AppendLine("[CART] ✓ Done");
    }

    // ── Player checks ─────────────────────────────────────────────────────────

    static void CheckPlayer(System.Text.StringBuilder log)
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { log.AppendLine("[PLAYER] Tag 'Player' not found!"); return; }
        log.AppendLine($"[PLAYER] Found: {player.name}");

        if (player.GetComponent<PlayerHandIK>() == null)
        {
            player.AddComponent<PlayerHandIK>();
            log.AppendLine("[PLAYER] Added PlayerHandIK");
        }
        else log.AppendLine("[PLAYER] PlayerHandIK OK");

        if (player.GetComponent<PlayerCarryLayer>() == null)
        {
            player.AddComponent<PlayerCarryLayer>();
            log.AppendLine("[PLAYER] Added PlayerCarryLayer");
        }
        else log.AppendLine("[PLAYER] PlayerCarryLayer OK");

        var pi = player.GetComponent<PlayerInteraction>();
        if (pi != null)
        {
            var f = typeof(PlayerInteraction).GetField("_range", Private);
            float range = f != null ? (float)f.GetValue(pi) : -1f;
            log.AppendLine($"[PLAYER] PlayerInteraction range = {range:F1}m");
        }
        else log.AppendLine("[PLAYER] PlayerInteraction: NOT FOUND");

        EditorUtility.SetDirty(player);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static Bounds ComputeBounds(GameObject root, Vector3 fallbackSize)
    {
        var rends = root.GetComponentsInChildren<Renderer>(false);
        if (rends.Length > 0)
        {
            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            return b;
        }
        return new Bounds(root.transform.position + Vector3.up, fallbackSize);
    }

    static Transform EnsureChild(Transform parent, string name,
        Vector3 worldPos, Quaternion worldRot)
    {
        var existing = parent.Find(name);
        if (existing != null) return existing;

        var go = new GameObject(name);
        go.transform.SetParent(parent);
        go.transform.position = worldPos;
        go.transform.rotation = worldRot;
        return go.transform;
    }

    // Uses reflection to set private [SerializeField] fields — avoids SerializedObject
    // initialization issues when the component was just added in the same frame.
    static void SetField(Component comp, string fieldName, object value)
    {
        var field = comp.GetType().GetField(fieldName, Private);
        if (field != null)
            field.SetValue(comp, value);
        else
            Debug.LogWarning($"[SetupVehicles] Field '{fieldName}' not found on {comp.GetType().Name}");
    }
}
