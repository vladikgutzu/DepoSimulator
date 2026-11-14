using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;

/// Editor tool: configures NetworkObject and NetworkVehicleSync on every CartController and
/// ReachTruckController in the active scene. NetworkVehicleSync is the sole transform authority;
/// keeping a NetworkTransform beside it duplicates local sampling and remote interpolation.
///
/// Menu: Tools → Warehouse Demo → Setup Network Vehicles (Multiplayer)
///
/// Safe to run multiple times — existing components are not duplicated.
public static class SetupNetworkVehicles
{
    [MenuItem("Tools/Warehouse Demo/Setup Network Vehicles (Multiplayer)")]
    static void Run()
    {
        int processed = 0;
        int skipped   = 0;

        var carts  = Object.FindObjectsByType<CartController>(FindObjectsSortMode.None);
        var trucks = Object.FindObjectsByType<ReachTruckController>(FindObjectsSortMode.None);

        foreach (var c in carts)  { if (SetupVehicle(c.gameObject))  processed++; else skipped++; }
        foreach (var t in trucks) { if (SetupVehicle(t.gameObject)) processed++; else skipped++; }

        EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log($"[SetupNetworkVehicles] Done — {processed} vehicles configured, {skipped} already had all components.");
    }

    static bool SetupVehicle(GameObject go)
    {
        bool changed = false;

        // 1. NetworkObject (required by NGO)
        if (go.GetComponent<NetworkObject>() == null)
        {
            go.AddComponent<NetworkObject>();
            changed = true;
        }

        // 2. Remove the obsolete transform stream. NetworkVehicleSync already samples the local
        // driver, validates snapshots on the server, and interpolates observers. Running an NGO
        // NetworkTransform as well makes the local owner serialize every tiny Rigidbody step a
        // second time and lets two systems drive the same transform on other peers.
        var nt = go.GetComponent<NetworkTransform>();
        if (nt != null)
        {
            Object.DestroyImmediate(nt);
            changed = true;
        }

        // 3. NetworkVehicleSync — occupancy, transform, and fork-height sync.
        if (go.GetComponent<NetworkVehicleSync>() == null)
        {
            go.AddComponent<NetworkVehicleSync>();
            changed = true;
        }

        EditorUtility.SetDirty(go);
        return changed;
    }
}
