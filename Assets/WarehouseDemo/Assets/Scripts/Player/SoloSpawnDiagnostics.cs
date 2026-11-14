using System.Collections;
using System.Text;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Prevents the offline scene player from receiving gravity before the build has a usable
/// physics surface below its spawn. Besides protecting the first frames, this writes enough
/// information to Player.log to distinguish a bad spawn from a missing build collider.
/// </summary>
public static class SoloSpawnDiagnostics
{
    const float FindPlayerTimeout = 3f;
    const float GroundTimeout = 4f;
    const float ProbeHeight = 1.5f;
    const float ProbeDistance = 8f;
    const int StableProbeCount = 2;

    static readonly RaycastHit[] Hits = new RaycastHit[32];

    public static IEnumerator WaitForGround()
    {
        float findDeadline = Time.realtimeSinceStartup + FindPlayerTimeout;
        ThirdPersonController player = null;

        while (player == null && Time.realtimeSinceStartup < findDeadline)
        {
            player = FindScenePlayer();
            if (player == null) yield return null;
        }

        if (player == null)
        {
            Debug.LogError($"[SoloSpawn] No active scene ThirdPersonController found after " +
                           $"{FindPlayerTimeout:0.0}s in '{SceneManager.GetActiveScene().path}'.");
            yield break;
        }

        bool wasLocked = player.MovementLocked;
        player.MovementLocked = true;

        var controller = player.GetComponent<CharacterController>();
        Vector3 initialPosition = player.transform.position;
        Debug.Log($"[SoloSpawn] Holding player while physics becomes ready. " +
                  $"scene='{SceneManager.GetActiveScene().path}', position={initialPosition}, " +
                  $"controllerEnabled={controller != null && controller.enabled}, " +
                  $"groundMask=0x{player.GroundLayers.value:X8}.");

        float groundDeadline = Time.realtimeSinceStartup + GroundTimeout;
        int stableProbes = 0;
        RaycastHit ground = default;

        while (Time.realtimeSinceStartup < groundDeadline)
        {
            // Static transforms are normally synchronised during scene activation. Calling this
            // explicitly makes the check deterministic when a fast standalone build reaches this
            // point before its first FixedUpdate.
            Physics.SyncTransforms();

            if (TryFindGround(player.transform, out ground))
            {
                stableProbes++;
                if (stableProbes >= StableProbeCount) break;
            }
            else
            {
                stableProbes = 0;
            }

            yield return new WaitForFixedUpdate();
        }

        if (stableProbes >= StableProbeCount)
        {
            player.MovementLocked = wasLocked;
            Debug.Log($"[SoloSpawn] Ground ready. collider='{HierarchyPath(ground.collider.transform)}', " +
                      $"type={ground.collider.GetType().Name}, layer={ground.collider.gameObject.layer}, " +
                      $"point={ground.point}, distance={ground.distance:0.000}, " +
                      $"waited={Mathf.Max(0f, GroundTimeout - (groundDeadline - Time.realtimeSinceStartup)):0.000}s.");
            yield break;
        }

        // Do not release gravity into an empty physics world. Keeping the player at the spawn is
        // safer and makes the actual collider failure visible instead of turning it into a fall
        // through the map. The log includes nearby colliders for build-only diagnosis.
        if (controller != null && controller.enabled)
        {
            controller.enabled = false;
            player.transform.position = initialPosition;
            controller.enabled = true;
        }
        else
        {
            player.transform.position = initialPosition;
        }

        Debug.LogError($"[SoloSpawn] Ground was NOT ready after {GroundTimeout:0.0}s. " +
                       $"Player remains movement-locked at {initialPosition}. " +
                       DescribeNearbyColliders(initialPosition, player.transform));
    }

    static ThirdPersonController FindScenePlayer()
    {
        foreach (var candidate in Object.FindObjectsByType<ThirdPersonController>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (candidate == null || !candidate.isActiveAndEnabled) continue;
            if (candidate.GetComponentInParent<NetworkPlayer>() != null) continue;
            return candidate;
        }

        return null;
    }

    static bool TryFindGround(Transform player, out RaycastHit nearest)
    {
        Vector3 origin = player.position + Vector3.up * ProbeHeight;
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, Hits, ProbeDistance,
            Physics.AllLayers, QueryTriggerInteraction.Ignore);

        nearest = default;
        float nearestDistance = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            var hit = Hits[i];
            if (hit.collider == null || hit.collider.transform.IsChildOf(player)) continue;
            if (hit.distance >= nearestDistance) continue;
            nearest = hit;
            nearestDistance = hit.distance;
        }

        return nearest.collider != null;
    }

    static string DescribeNearbyColliders(Vector3 position, Transform player)
    {
        var colliders = Physics.OverlapSphere(position, 6f, Physics.AllLayers,
            QueryTriggerInteraction.Ignore);
        var text = new StringBuilder($"Nearby non-trigger colliders={colliders.Length}:");
        int written = 0;

        foreach (var collider in colliders)
        {
            if (collider == null || collider.transform.IsChildOf(player)) continue;
            text.Append($" [{HierarchyPath(collider.transform)}; {collider.GetType().Name}; " +
                        $"layer={collider.gameObject.layer}; enabled={collider.enabled}]");
            if (++written >= 12) break;
        }

        return text.ToString();
    }

    static string HierarchyPath(Transform transform)
    {
        if (transform == null) return "<null>";
        var path = new StringBuilder(transform.name);
        while (transform.parent != null)
        {
            transform = transform.parent;
            path.Insert(0, transform.name + "/");
        }
        return path.ToString();
    }
}
