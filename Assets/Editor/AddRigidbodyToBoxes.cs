using UnityEngine;
using UnityEditor;

public static class AddRigidbodyToBoxes
{
    const float Density  = 100f;
    const float MinMass  = 0.5f;
    const float MaxMass  = 120f;

    static bool IsBox(GameObject go) =>
        go.name.IndexOf("box", System.StringComparison.OrdinalIgnoreCase) >= 0;

    // Pallet root: has PalletBehavior — must NEVER be touched by this tool.
    static bool IsPalletRoot(GameObject go) =>
        go.GetComponent<PalletBehavior>() != null;

    // An organisational grouping node:
    //   • has "box" in its name
    //   • has NO Renderer or Collider directly on itself (pure Transform container)
    //   • has at least one direct child whose name contains "box"
    static bool IsOrganisationalNode(GameObject go)
    {
        if (!IsBox(go)) return false;
        if (go.GetComponent<Renderer>() != null) return false;
        if (go.GetComponent<Collider>()  != null) return false;

        for (int i = 0; i < go.transform.childCount; i++)
            if (IsBox(go.transform.GetChild(i).gameObject)) return true;

        return false;
    }

    // ── Add Rigidbody to Boxes ────────────────────────────────────────────────

    [MenuItem("Tools/Warehouse Demo/Add Rigidbody to Boxes")]
    static void Execute()
    {
        var allObjects = Object.FindObjectsOfType<GameObject>();
        int added = 0, updated = 0, skipped = 0, cleaned = 0;

        foreach (var go in allObjects)
        {
            // Pallet roots own their own Rigidbody via PalletBehavior — skip entirely.
            if (IsPalletRoot(go)) continue;

            // ── Organisational nodes (box_Level_02, box_grp, …) ─────────────────
            if (IsOrganisationalNode(go))
            {
                var bpi = go.GetComponent<BoxPhysicsInteractable>();
                if (bpi != null) { Undo.DestroyObjectImmediate(bpi); cleaned++; }

                var rb = go.GetComponent<Rigidbody>();
                if (rb != null) { Undo.DestroyObjectImmediate(rb); cleaned++; }

                continue;
            }

            if (!IsBox(go)) continue;

            // ── Actual box objects (box_grp1..N, box_Level_01, …) ───────────────
            var bounds = GetBounds(go);
            if (bounds.size == Vector3.zero) { skipped++; continue; }

            float volume = bounds.size.x * bounds.size.y * bounds.size.z;
            float mass   = Mathf.Clamp(volume * Density, MinMass, MaxMass);

            var existingRb = go.GetComponent<Rigidbody>();
            bool isNew     = existingRb == null;
            var  rb2       = isNew ? Undo.AddComponent<Rigidbody>(go) : existingRb;

            if (!isNew) Undo.RecordObject(rb2, "Update Rigidbody");

            rb2.mass                   = mass;
            rb2.isKinematic            = true;
            rb2.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb2.interpolation          = RigidbodyInterpolation.None;

            foreach (var mc in go.GetComponentsInChildren<MeshCollider>())
            {
                Undo.RecordObject(mc, "Make MeshCollider Convex");
                mc.convex = true;
            }

            if (go.GetComponent<BoxPhysicsInteractable>() == null)
                Undo.AddComponent<BoxPhysicsInteractable>(go);

            EditorUtility.SetDirty(go);
            Debug.Log($"[BoxRb] {go.name}: {bounds.size.x:F2}×{bounds.size.y:F2}×{bounds.size.z:F2} м  " +
                      $"масса={mass:F1} кг  {(isNew ? "добавлен" : "обновлён")}");

            if (isNew) added++; else updated++;
        }

        Debug.Log($"[BoxRb] Готово — добавлено: {added}, обновлено: {updated}, " +
                  $"очищено промежуточных: {cleaned}, пропущено: {skipped}");
    }

    // ── Fix Pallet Rigidbodies ────────────────────────────────────────────────
    // Restores Rigidbodies that were accidentally removed from pallet roots
    // by a previous run of Add Rigidbody to Boxes.

    [MenuItem("Tools/Warehouse Demo/Fix Pallet Rigidbodies")]
    static void FixPallets()
    {
        var allObjects = Object.FindObjectsOfType<GameObject>();
        int fixed_ = 0, cleaned = 0;

        foreach (var go in allObjects)
        {
            var pb = go.GetComponent<PalletBehavior>();
            if (pb == null) continue;

            // Remove stray BPI that the old tool may have added to the pallet root.
            var bpi = go.GetComponent<BoxPhysicsInteractable>();
            if (bpi != null) { Undo.DestroyObjectImmediate(bpi); cleaned++; }

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                // Rigidbody was stripped — recreate it with correct settings.
                rb = Undo.AddComponent<Rigidbody>(go);
                Debug.Log($"[FixPallets] Restored Rigidbody on {go.name}");
                fixed_++;
            }
            else
            {
                Undo.RecordObject(rb, "Fix pallet Rigidbody");
            }

            // Pallets rest on the floor by default — dynamic with gravity.
            rb.isKinematic            = false;
            rb.useGravity             = true;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.interpolation          = RigidbodyInterpolation.Interpolate;
            rb.constraints            = RigidbodyConstraints.FreezeRotation;
            rb.linearVelocity         = Vector3.zero;
            rb.angularVelocity        = Vector3.zero;

            EditorUtility.SetDirty(go);
        }

        Debug.Log($"[FixPallets] Готово — восстановлено: {fixed_}, лишних BPI удалено: {cleaned}");
    }

    // ─────────────────────────────────────────────────────────────────────────

    static Bounds GetBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            var b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }
        var colliders = go.GetComponentsInChildren<Collider>();
        if (colliders.Length > 0)
        {
            var b = colliders[0].bounds;
            for (int i = 1; i < colliders.Length; i++) b.Encapsulate(colliders[i].bounds);
            return b;
        }
        return new Bounds();
    }
}
