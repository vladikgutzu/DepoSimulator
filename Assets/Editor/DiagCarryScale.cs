using UnityEngine;

public class DiagCarryScale
{
    public static void Execute()
    {
        // Find all BoxPhysicsInteractable and print _carryMaxSize via reflection
        var boxes = Object.FindObjectsByType<BoxPhysicsInteractable>(FindObjectsSortMode.None);
        Debug.Log($"[DiagCarryScale] Found {boxes.Length} BoxPhysicsInteractable components");

        int printed = 0;
        foreach (var box in boxes)
        {
            // Get _carryMaxSize via reflection
            var field = typeof(BoxPhysicsInteractable)
                .GetField("_carryMaxSize",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            float carryMaxSize = field != null ? (float)field.GetValue(box) : -1f;

            // Get world-space bounds from renderers
            var rends = box.GetComponentsInChildren<Renderer>(false);
            if (rends.Length == 0) continue;

            var b = rends[0].bounds;
            foreach (var r in rends) b.Encapsulate(r.bounds);
            float maxDim = Mathf.Max(b.size.x, b.size.y, b.size.z);

            Debug.Log($"[DiagCarryScale] '{box.gameObject.name}' | _carryMaxSize={carryMaxSize} | worldBounds max={maxDim:F3} ({b.size.x:F3} x {b.size.y:F3} x {b.size.z:F3}) | localScale={box.transform.localScale} | wouldScale={maxDim > carryMaxSize}");

            if (++printed >= 5) break; // only first 5 unique boxes
        }
    }
}
