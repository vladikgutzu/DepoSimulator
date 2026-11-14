using UnityEngine;
using UnityEditor;

// Runs once, fires a ray from camera center and reports what PlayerInteraction would see.
public static class DiagInteractionRay
{
    public static void Execute()
    {
        var cam = Camera.main;
        if (cam == null) { Debug.Log("[DiagRay] No Camera.main found"); return; }

        var screenCenter = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f);
        var camRay       = cam.ScreenPointToRay(screenCenter);
        var mask         = ~LayerMask.GetMask("Outline");

        // Two-step TPS ray (mirrors PlayerInteraction.Scan fix)
        var aimPoint = camRay.GetPoint(13.5f);
        if (Physics.Raycast(camRay, out var aimHit, 13.5f, mask, QueryTriggerInteraction.Ignore))
            aimPoint = aimHit.point;

        var player = GameObject.FindGameObjectWithTag("Player");
        Ray ray;
        if (player != null)
        {
            var eye = player.transform.position + Vector3.up * 1.6f;
            ray = new Ray(eye, (aimPoint - eye).normalized);
            Debug.Log($"[DiagRay] Eye origin={eye}  aimPoint={aimPoint}");
        }
        else
        {
            ray = camRay;
            Debug.Log("[DiagRay] No Player tag found — using raw camera ray");
        }

        bool uiBlocked = UIManager.IsAnyPanelOpen;
        Debug.Log($"[DiagRay] UIManager.IsAnyPanelOpen = {uiBlocked}");

        // Fire a LONG ray (20m) to see everything in front
        RaycastHit[] hits = Physics.RaycastAll(ray, 20f, mask, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        if (hits.Length == 0)
        {
            Debug.Log("[DiagRay] RaycastAll hit NOTHING in 20m (no non-trigger colliders)");
        }
        else
        {
            for (int i = 0; i < Mathf.Min(hits.Length, 5); i++)
            {
                var h   = hits[i];
                var bpi = h.collider.GetComponentInParent<BoxPhysicsInteractable>();
                var pb  = h.collider.GetComponentInParent<PalletBehavior>();
                var col = h.collider;
                Debug.Log($"[DiagRay] [{i}] dist={h.distance:F2}m  obj='{h.collider.gameObject.name}'" +
                          $"  collider={col.GetType().Name} isTrigger={col.isTrigger}" +
                          $"  BPI={(bpi != null ? "YES" : "none")}  Pallet={(pb != null ? "YES" : "none")}" +
                          $"  inRange={(h.distance <= 4.5f ? "YES" : "TOO FAR")}");
            }
        }

        // Check outline shader
        var outlineMat = Resources.Load<Material>("FPS_Outline_Can");
        if (outlineMat == null)
            Debug.Log("[DiagRay] FPS_Outline_Can.mat NOT found in Resources");
        else
            Debug.Log($"[DiagRay] FPS_Outline_Can.mat shader = '{outlineMat.shader.name}'  " +
                      $"valid={outlineMat.shader != null && outlineMat.shader.isSupported}");
    }
}
