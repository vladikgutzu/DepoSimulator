using UnityEngine;

/// Add this component to objects (AssemblyBench, OrderBoard) that should display
/// a pulsing floor border square instead of a mesh outline when highlighted by
/// TutorialHighlighter.
public class FloorHighlightTarget : MonoBehaviour
{
    [Tooltip("World-space width override. 0 = auto-fit from collider bounds.")]
    [SerializeField] float _widthOverride = 0f;
    [Tooltip("World-space depth override. 0 = auto-fit from collider bounds.")]
    [SerializeField] float _depthOverride = 0f;
    [Tooltip("Padding added to each side of the auto-computed footprint.")]
    [SerializeField] float _padding = 0.3f;
    [Tooltip("Offset above the detected floor surface.")]
    [SerializeField] float _yOffset = 0.02f;

    public float WidthOverride => _widthOverride;
    public float DepthOverride => _depthOverride;
    public float Padding       => _padding;
    public float YOffset       => _yOffset;
}
