using UnityEngine;

public static class TestBoxGrab
{
    public static void Execute()
    {
        // Find player and box
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null) { Debug.LogError("[TestBoxGrab] Player not found"); return; }

        var box = GameObject.Find("Warehouse/Props/box_large_prefab");
        if (box == null) { Debug.LogError("[TestBoxGrab] Box not found"); return; }

        // Teleport player directly in front of the box
        var boxPos   = box.transform.position;
        var standPos = boxPos + new Vector3(0f, 0f, -1.5f);
        player.transform.position = standPos;
        player.transform.rotation = Quaternion.LookRotation(boxPos - standPos, Vector3.up);

        // Call Interact directly
        var interactable = box.GetComponent<IInteractable>();
        if (interactable == null) { Debug.LogError("[TestBoxGrab] No IInteractable on box"); return; }

        interactable.Interact(player);
        Debug.Log($"[TestBoxGrab] Player at {standPos}, grabbed box at {boxPos}");
    }
}
