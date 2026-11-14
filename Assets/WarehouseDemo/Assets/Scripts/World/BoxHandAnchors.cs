using UnityEngine;

// Reference holder for hand IK targets on box prefabs.
// Positions/rotations are set directly on HandTarget_R and HandTarget_L
// in the prefab — this script just exposes them to BoxPhysicsInteractable.
public class BoxHandAnchors : MonoBehaviour
{
    [Header("IK Targets (children of this prefab)")]
    [SerializeField] Transform _rightHandTarget;
    [SerializeField] Transform _leftHandTarget;

    [Header("Optional Carry Pose")]
    [Tooltip("Backward upper-body lean used only while carrying this box prefab.")]
    [SerializeField, Range(0f, 70f)] float _carryLean;

    public Transform RightHandTarget => _rightHandTarget;
    public Transform LeftHandTarget  => _leftHandTarget;
    public float CarryLean           => _carryLean;

    /// Repositions the grips from the box's actual rendered bounds. Packed orders can be much
    /// taller or wider than their source prefab, so authored anchors are only sensible for the
    /// original carton. Both local and remote characters call this and therefore use the same
    /// pose for an arbitrarily-sized package.
    public Vector3 ConfigureFromBounds()
    {
        var renderers = GetComponentsInChildren<Renderer>(false);
        if (renderers.Length == 0) return Vector3.one;

        EnsureTargets();

        Bounds local = default;
        bool hasBounds = false;
        foreach (var renderer in renderers)
        {
            if (renderer == null) continue;
            Bounds source = renderer.localBounds;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 corner = new(source.center.x + source.extents.x * x,
                                     source.center.y + source.extents.y * y,
                                     source.center.z + source.extents.z * z);
                Vector3 p = transform.InverseTransformPoint(
                    renderer.transform.TransformPoint(corner));
                if (!hasBounds) { local = new Bounds(p, Vector3.zero); hasBounds = true; }
                else local.Encapsulate(p);
            }
        }

        if (!hasBounds) return Vector3.one;

        float sideInset = Mathf.Min(local.extents.x * 0.18f, 0.10f);
        // Packed orders are supported from underneath rather than pinched on the front face.
        // Keep the palms just below the bottom plane and slightly in from its carrier-facing edge,
        // with one hand near each lower corner.
        float gripY = local.min.y - 0.035f;
        float gripZ = Mathf.Lerp(local.min.z, local.max.z, 0.20f);
        Vector3 rightGrip = new(local.max.x - sideInset, gripY, gripZ);
        Vector3 leftGrip  = new(local.min.x + sideInset, gripY, gripZ);

        // Authored targets are sometimes nested under a mesh child. Assigning the root-local
        // coordinates to target.localPosition made those parents transform the value a second
        // time, lifting both hands onto the front face. Convert through the box root into world
        // space so the targets land on the actual bottom plane regardless of their parent.
        _rightHandTarget.position = transform.TransformPoint(rightGrip);
        _leftHandTarget.position  = transform.TransformPoint(leftGrip);
        return local.size;
    }

    void EnsureTargets()
    {
        if (_rightHandTarget == null)
        {
            _rightHandTarget = new GameObject("HandTarget_R_Runtime").transform;
            _rightHandTarget.SetParent(transform, false);
            _rightHandTarget.localRotation = Quaternion.Euler(0f, 90f, 90f);
        }
        if (_leftHandTarget == null)
        {
            _leftHandTarget = new GameObject("HandTarget_L_Runtime").transform;
            _leftHandTarget.SetParent(transform, false);
            _leftHandTarget.localRotation = Quaternion.Euler(0f, -90f, -90f);
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (_rightHandTarget != null)
        {
            Gizmos.color = new Color(0.2f, 0.5f, 1f);
            Gizmos.DrawSphere(_rightHandTarget.position, 0.045f);
            Gizmos.DrawLine(transform.position, _rightHandTarget.position);
        }
        if (_leftHandTarget != null)
        {
            Gizmos.color = new Color(1f, 0.3f, 0.2f);
            Gizmos.DrawSphere(_leftHandTarget.position, 0.045f);
            Gizmos.DrawLine(transform.position, _leftHandTarget.position);
        }
    }
#endif
}
