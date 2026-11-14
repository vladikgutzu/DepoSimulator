using UnityEngine;
using UnityEditor;
using System.Text;

public static class AuditPallets
{
    public static void Execute()
    {
        var all = Object.FindObjectsOfType<GameObject>();
        var sb  = new StringBuilder();

        int total = 0, noRb = 0, wrongRb = 0, noGrabCol = 0, underground = 0, noBpi = 0;

        foreach (var go in all)
        {
            var pb = go.GetComponent<PalletBehavior>();
            if (pb == null) continue;
            total++;

            var issues = new System.Collections.Generic.List<string>();

            // Rigidbody
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
            {
                issues.Add("NO Rigidbody");
                noRb++;
            }
            else
            {
                if (rb.isKinematic)  issues.Add("isKinematic=true (should be false at rest)");
                if (!rb.useGravity)  issues.Add("useGravity=false");
                if (rb.constraints == RigidbodyConstraints.None)
                    issues.Add("no FreezeRotation constraint");
                if (rb.isKinematic || !rb.useGravity) wrongRb++;
            }

            // GrabCollider
            if (pb.GrabCollider == null) { issues.Add("NO GrabCollider"); noGrabCol++; }

            // Underground
            if (go.transform.position.y < -0.1f)
            {
                issues.Add($"UNDERGROUND y={go.transform.position.y:F3}");
                underground++;
            }

            // Child BPI count
            var bpiList = go.GetComponentsInChildren<BoxPhysicsInteractable>(true);
            if (bpiList.Length == 0) { issues.Add("no child BPI"); noBpi++; }

            // Child RBs
            var childRbs = new System.Collections.Generic.List<Rigidbody>();
            foreach (var r in go.GetComponentsInChildren<Rigidbody>(true))
                if (r.gameObject != go) childRbs.Add(r);

            int detectOn = 0;
            foreach (var r in childRbs)
                if (r.detectCollisions) detectOn++;
            if (detectOn > 0)
                issues.Add($"{detectOn}/{childRbs.Count} child RBs have detectCollisions=true");

            if (issues.Count > 0)
                sb.AppendLine($"[ISSUE] {go.name} @ {go.transform.position}: {string.Join(", ", issues)}");
        }

        sb.Insert(0, $"[PalletAudit] Total: {total}  noRB: {noRb}  wrongRB: {wrongRb}  " +
                     $"noGrabCol: {noGrabCol}  underground: {underground}  noBPI: {noBpi}\n");

        Debug.Log(sb.ToString());
    }
}
