using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SetupTruckRigidbody
{
    public static void Execute()
    {
        var truck = GameObject.Find("Reach_Truck_01a_Rig_Prefab_01");
        if (truck == null) { Debug.LogError("[TruckRb] Truck not found in scene!"); return; }

        var rb = truck.GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = Undo.AddComponent<Rigidbody>(truck);
            Debug.Log("[TruckRb] Rigidbody added.");
        }
        else
        {
            Debug.Log("[TruckRb] Rigidbody already exists, reconfiguring.");
        }

        rb.isKinematic   = false;
        rb.useGravity    = false;
        rb.linearDamping = 8f;
        rb.angularDamping= 10f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.constraints   = RigidbodyConstraints.FreezeRotationX
                         | RigidbodyConstraints.FreezeRotationZ
                         | RigidbodyConstraints.FreezePositionY;

        EditorUtility.SetDirty(truck);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log($"[TruckRb] Done. isKinematic={rb.isKinematic}, constraints={rb.constraints}");
    }
}
