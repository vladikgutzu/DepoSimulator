using UnityEditor;
using UnityEngine;

public class SetAssemblyStages
{
    public static void Execute()
    {
        var guids = AssetDatabase.FindAssets("t:OrderDefinition");
        int changed = 0;

        foreach (var guid in guids)
        {
            string path  = AssetDatabase.GUIDToAssetPath(guid);
            var    order = AssetDatabase.LoadAssetAtPath<OrderDefinition>(path);
            if (order == null) continue;

            var so    = new SerializedObject(order);
            var parts = so.FindProperty("RequiredParts");
            if (parts == null || !parts.isArray) continue;

            bool dirty = false;

            // moto_drive: motor(0) → stage 0; cables(1) → stage 1; cable_housing(2) → stage 1
            if (order.name.Contains("moto_drive"))
            {
                int[] stages = { 0, 1, 1 };
                dirty = ApplyStages(parts, stages);
            }
            // moto_frame: body_main(0) → stage 0; body_front(1) → stage 0; frame_cover(2) → stage 1; bridge(3) → stage 1
            else if (order.name.Contains("moto_frame"))
            {
                int[] stages = { 0, 0, 1, 1 };
                dirty = ApplyStages(parts, stages);
            }
            // moto_arms: arm_chassis(0) → 0; chassis_support(1) → 0; claw(2) → 1;
            //            inner_shaft(3) → 1; outer_shaft(4) → 1; gliders_left(5) → 2; gliders_right(6) → 2
            else if (order.name.Contains("moto_arms"))
            {
                int[] stages = { 0, 0, 1, 1, 1, 2, 2 };
                dirty = ApplyStages(parts, stages);
            }

            if (dirty)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(order);
                changed++;
                Debug.Log($"[SetAssemblyStages] Configured stages for: {order.name} ({path})");
            }
        }

        AssetDatabase.SaveAssets();
        Debug.Log($"[SetAssemblyStages] Done. {changed} order(s) updated.");
    }

    static bool ApplyStages(SerializedProperty partsArray, int[] stages)
    {
        bool changed = false;
        for (int i = 0; i < Mathf.Min(partsArray.arraySize, stages.Length); i++)
        {
            var elem  = partsArray.GetArrayElementAtIndex(i);
            var stageProp = elem.FindPropertyRelative("AssemblyStage");
            if (stageProp == null) continue;
            if (stageProp.intValue != stages[i])
            {
                stageProp.intValue = stages[i];
                changed = true;
            }
        }
        return changed;
    }
}
