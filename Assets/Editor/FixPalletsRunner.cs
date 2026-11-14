using UnityEngine;
using UnityEditor;

public static class FixPalletsRunner
{
    public static void Execute()
    {
        var allObjects = Object.FindObjectsOfType<GameObject>();
        int fixed_ = 0, cleaned = 0;

        foreach (var go in allObjects)
        {
            var pb = go.GetComponent<PalletBehavior>();
            if (pb == null) continue;

            var bpi = go.GetComponent<BoxPhysicsInteractable>();
            if (bpi != null) { Undo.DestroyObjectImmediate(bpi); cleaned++; }

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = Undo.AddComponent<Rigidbody>(go);
                Debug.Log($"[FixPallets] Restored Rigidbody on {go.name}");
                fixed_++;
            }
            else
            {
                Undo.RecordObject(rb, "Fix pallet Rigidbody");
            }

            rb.isKinematic            = false;
            rb.useGravity             = true;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.interpolation          = RigidbodyInterpolation.Interpolate;
            rb.constraints            = RigidbodyConstraints.FreezeRotation;
            rb.linearVelocity         = Vector3.zero;
            rb.angularVelocity        = Vector3.zero;

            // Disable detectCollisions on child box RBs so kinematic boxes
            // don't push the dynamic pallet through depenetration forces.
            foreach (var childRb in go.GetComponentsInChildren<Rigidbody>(true))
            {
                if (childRb.gameObject == go) continue;
                childRb.detectCollisions = false;
            }

            EditorUtility.SetDirty(go);
        }

        Debug.Log($"[FixPallets] Готово — восстановлено: {fixed_}, лишних BPI удалено: {cleaned}");
    }
}
