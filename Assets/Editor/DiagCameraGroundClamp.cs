using UnityEngine;
using UnityEditor;

// Tools > Warehouse Demo > Diag Camera Ground Clamp
// Simulates the TPSCameraController geometric floor-clamp math for pitch
// values from 0 to -89 degrees (looking up) and logs whether the camera
// would stay above the player's feet plane.
public static class DiagCameraGroundClamp
{
    [MenuItem("Tools/Warehouse Demo/Diag Camera Ground Clamp")]
    public static void Execute()
    {
        // Mirror the default field values from TPSCameraController
        const float followDistance   = 3.5f;
        const float minDistance      = 0.6f;
        const float pivotHeight      = 1.55f;
        const float shoulderOffsetY  = 0.12f;
        const float minCameraHeight  = 0.15f;
        const float playerY          = 0f;  // player standing on ground

        float shoulderPivotY = playerY + pivotHeight + shoulderOffsetY;
        float floorY         = playerY + minCameraHeight;

        bool anyViolation = false;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Camera Ground Clamp Diagnostic ===");
        sb.AppendLine($"shoulderPivotY={shoulderPivotY:F3}  floorY={floorY:F3}");
        sb.AppendLine($"{"Pitch",8}  {"camDir.y",10}  {"maxDistFloor",14}  {"usedDist",10}  {"camPosY",10}  {"OK?",6}");

        for (int pitchDeg = 0; pitchDeg >= -89; pitchDeg -= 5)
        {
            float pitch  = pitchDeg * Mathf.Deg2Rad;
            // camDir = Quaternion.Euler(pitchDeg, yaw, 0) * Vector3.back
            // For pure pitch (yaw=0): camDir.y = sin(pitch), camDir.z = -cos(pitch)
            float camDirY = Mathf.Sin(pitch);   // negative when looking up (pitch < 0)

            float maxDistFloor = float.MaxValue;
            if (camDirY < -0.001f)
            {
                float headroom = shoulderPivotY - floorY;
                maxDistFloor = Mathf.Max(minDistance, headroom / (-camDirY));
            }

            float usedDist = Mathf.Min(followDistance, maxDistFloor);
            float camPosY  = shoulderPivotY + camDirY * usedDist;
            bool  ok       = camPosY >= floorY - 0.001f;

            if (!ok) anyViolation = true;

            sb.AppendLine($"{pitchDeg,8}°  {camDirY,10:F4}  {maxDistFloor,14:F3}  {usedDist,10:F3}  {camPosY,10:F3}  {(ok ? "OK" : "FAIL"),6}");
        }

        sb.AppendLine();
        sb.AppendLine(anyViolation
            ? "❌ VIOLATIONS FOUND — camera can go underground"
            : "✅ All pitch values pass — camera stays above floor");

        Debug.Log(sb.ToString());
        EditorUtility.DisplayDialog("Camera Ground Clamp Diagnostic",
            anyViolation
                ? "FAIL: camera can still go underground.\nSee Console for details."
                : "PASS: camera stays above floor for all pitch values (0° to -89°).",
            "OK");
    }
}
