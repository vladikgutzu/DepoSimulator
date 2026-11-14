using UnityEngine;
using UnityEditor;

public class ToggleBoxPhysics
{
    public static void MakeDynamic()
    {
        int count = 0;
        foreach (var box in Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsSortMode.None))
        {
            var rb = box.GetComponent<Rigidbody>();
            if (rb == null) continue;
            Undo.RecordObject(rb, "Make Box Dynamic");
            rb.isKinematic = false;
            rb.useGravity  = true;
            EditorUtility.SetDirty(rb);
            count++;
        }
        Debug.Log($"[ToggleBoxPhysics] {count} boxes → dynamic (non-kinematic)");
    }

    public static void MakeKinematic()
    {
        int count = 0;
        foreach (var box in Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsSortMode.None))
        {
            var rb = box.GetComponent<Rigidbody>();
            if (rb == null) continue;
            Undo.RecordObject(rb, "Make Box Kinematic");
            rb.isKinematic = true;
            rb.useGravity  = false;
            EditorUtility.SetDirty(rb);
            count++;
        }
        Debug.Log($"[ToggleBoxPhysics] {count} boxes → kinematic");
    }
}
