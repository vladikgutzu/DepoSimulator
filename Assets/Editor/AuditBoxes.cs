using UnityEngine;
using UnityEditor;
using System.Text;

public static class AuditBoxes
{
    public static void Execute()
    {
        var sb = new StringBuilder();
        int total = 0, noBpi = 0, noRb = 0, noCol = 0, colDisabled = 0, detectOff = 0;

        foreach (var bpi in Object.FindObjectsOfType<BoxPhysicsInteractable>(true))
        {
            total++;
            var go  = bpi.gameObject;
            var rb  = go.GetComponent<Rigidbody>();
            var cols = go.GetComponentsInChildren<Collider>(true);

            var issues = new System.Collections.Generic.List<string>();

            if (rb == null)                  { issues.Add("NO Rigidbody"); noRb++; }
            else if (!rb.detectCollisions)   { issues.Add("detectCollisions=false"); detectOff++; }

            bool anyEnabled = false;
            foreach (var c in cols) if (c.enabled) { anyEnabled = true; break; }
            if (cols.Length == 0)  { issues.Add("NO Collider"); noCol++; }
            else if (!anyEnabled)  { issues.Add("all Colliders disabled"); colDisabled++; }

            // Check parent chain for PalletBehavior (box is on a pallet)
            bool onPallet = go.GetComponentInParent<PalletBehavior>() != null;

            if (issues.Count > 0)
                sb.AppendLine($"[ISSUE] {go.name}  onPallet={onPallet}  @ {go.transform.position}: {string.Join(", ", issues)}");
        }

        // Find "box" objects that have NO BPI at all
        foreach (var go in Object.FindObjectsOfType<GameObject>())
        {
            if (!go.name.StartsWith("box", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (go.GetComponent<BoxPhysicsInteractable>() != null) continue;
            if (go.GetComponent<Rigidbody>() == null) continue; // must have RB to matter
            if (go.GetComponent<PalletBehavior>() != null) continue; // skip pallet roots
            noBpi++;
            sb.AppendLine($"[NO BPI] {go.name} has Rigidbody but no BoxPhysicsInteractable @ {go.transform.position}");
        }

        sb.Insert(0, $"[BoxAudit] Total BPI: {total}  noRB: {noRb}  detectOff: {detectOff}  " +
                     $"noCol: {noCol}  colDisabled: {colDisabled}  noBPI: {noBpi}\n");
        Debug.Log(sb.ToString());
    }
}
