using UnityEngine;

/// Late-frame humanoid override used while a robot is docked at a charger. Animator remains
/// enabled, so locomotion and NetworkAnimator keep updating normally underneath the smooth pose.
public static class ChargingPoseUtility
{
    public const float BlendSeconds = 0.45f;

    public static void Apply(Animator animator, float weight)
    {
        if (animator == null || !animator.isHuman || weight <= 0f) return;

        Transform leftUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform leftLower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        Transform leftHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
        Transform rightUpper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform rightLower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        Transform rightHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        if (leftUpper == null || leftLower == null || leftHand == null ||
            rightUpper == null || rightLower == null || rightHand == null || hips == null)
            return;

        Vector3 up = hips.up;
        Vector3 right = hips.right;
        Vector3 leftDirection = (-right * 0.65f - up * 0.76f).normalized;
        Vector3 rightDirection = (right * 0.65f - up * 0.76f).normalized;

        AlignBone(leftUpper, leftLower, leftDirection, weight);
        AlignBone(leftLower, leftHand, leftDirection, weight);
        AlignBone(rightUpper, rightLower, rightDirection, weight);
        AlignBone(rightLower, rightHand, rightDirection, weight);
    }

    static void AlignBone(Transform bone, Transform child, Vector3 desiredDirection, float weight)
    {
        Vector3 currentDirection = child.position - bone.position;
        if (currentDirection.sqrMagnitude < 0.000001f) return;
        Quaternion target = Quaternion.FromToRotation(
            currentDirection.normalized, desiredDirection) * bone.rotation;
        bone.rotation = Quaternion.Slerp(bone.rotation, target, Mathf.Clamp01(weight));
    }
}
