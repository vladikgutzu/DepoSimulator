using UnityEngine;
using UnityEditor;

public class CheckSteeringAxes
{
    public static void Execute()
    {
        var go = GameObject.Find("Reach_Truck_01a_Rig_Prefab_01");
        if (go == null) { Debug.LogError("Reach_Truck_01a_Rig_Prefab_01 not found"); return; }

        var t = go.transform.Find("Reach_Truck_01a_Rig/Root/Body/Steering_Wheel");
        if (t == null) { Debug.LogError("Steering_Wheel bone not found"); return; }

        Debug.Log($"[SteeringWheel] worldPos={t.position}");
        Debug.Log($"[SteeringWheel] localRight   (X) in WORLD = {t.right}");
        Debug.Log($"[SteeringWheel] localUp      (Y) in WORLD = {t.up}");
        Debug.Log($"[SteeringWheel] localForward (Z) in WORLD = {t.forward}");

        // DriverSeat
        var seat = go.transform.Find("DriverSeat");
        if (seat != null)
        {
            Debug.Log($"[DriverSeat] worldPos={seat.position}  worldFwd={seat.forward}  worldRight={seat.right}");
            Vector3 toWheel = t.position - seat.position;
            Debug.Log($"[DriverSeat->Wheel] direction={toWheel.normalized}");
        }
    }
}
