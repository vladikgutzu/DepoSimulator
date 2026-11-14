using UnityEngine;

public static class DiagBoxCarry
{
    public static void Execute()
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { Debug.LogError("[Diag] Player not found"); return; }

        var anim = player.GetComponentInChildren<Animator>();
        if (anim == null) { Debug.LogError("[Diag] No Animator"); return; }

        int isCarryingHash = Animator.StringToHash("IsCarryingBox");
        bool isCarrying    = anim.GetBool(isCarryingHash);
        int  upperBodyIdx  = anim.GetLayerIndex("UpperBody");
        float upperWeight  = upperBodyIdx >= 0 ? anim.GetLayerWeight(upperBodyIdx) : -1f;

        var ik = player.GetComponent<PlayerHandIK>();
        float ikWeight = ik != null
            ? (float)typeof(PlayerHandIK)
                .GetField("_weight", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(ik)
            : -1f;
        object rightTarget = ik != null
            ? typeof(PlayerHandIK)
                .GetField("_rightTarget", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(ik)
            : null;

        Debug.Log($"[Diag] IsCarryingBox={isCarrying}  UpperBodyIdx={upperBodyIdx}  UpperBodyWeight={upperWeight:F3}  IK_weight={ikWeight:F3}  IK_rightTarget={rightTarget}");

        // Check IK pass on layers
        for (int i = 0; i < anim.layerCount; i++)
            Debug.Log($"[Diag]   Layer {i} ({anim.GetLayerName(i)}): weight={anim.GetLayerWeight(i):F3}");
    }
}
