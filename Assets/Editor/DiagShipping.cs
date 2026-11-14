using UnityEngine;
using System.Reflection;

public class DiagShipping
{
    public static void Execute()
    {
        var zone = Object.FindAnyObjectByType<ShippingZone>();
        if (zone == null) { Debug.LogError("[DiagShipping] ShippingZone NOT FOUND"); return; }

        var fActive    = typeof(ShippingZone).GetField("_zoneActive",    BindingFlags.NonPublic | BindingFlags.Instance);
        var fMarker    = typeof(ShippingZone).GetField("_markerRoot",    BindingFlags.NonPublic | BindingFlags.Instance);
        var fGates     = typeof(ShippingZone).GetField("_gateFloors",    BindingFlags.NonPublic | BindingFlags.Instance);

        bool  active  = fActive  != null && (bool)fActive.GetValue(zone);
        var   marker  = fMarker  != null ? fMarker.GetValue(zone) as GameObject : null;
        var   gates   = fGates   != null ? fGates.GetValue(zone) as Transform[] : null;

        Debug.Log($"[DiagShipping] position={zone.transform.position} | _zoneActive={active}");
        Debug.Log($"[DiagShipping] _markerRoot={marker?.name ?? "NULL"} active={marker?.activeSelf}");
        Debug.Log($"[DiagShipping] _gateFloors count={gates?.Length ?? -1}");

        var bench = Object.FindAnyObjectByType<AssemblyBench>();
        if (bench != null)
            Debug.Log($"[DiagShipping] AssemblyBench.IsOrderComplete={bench.IsOrderComplete()} | ActiveOrder={bench.ActiveOrder?.name ?? "null"}");
        else
            Debug.LogError("[DiagShipping] AssemblyBench NOT FOUND");
    }
}
