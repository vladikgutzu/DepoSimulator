using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class TestClickSinglePlayer
{
    public static void Execute()
    {
        var buttons = Object.FindObjectsByType<Button>(FindObjectsSortMode.None);
        foreach (var btn in buttons)
        {
            string txt = btn.GetComponentInChildren<TMP_Text>()?.text ?? "";
            if (txt.ToUpper().Contains("ОДИНОЧНАЯ") || txt.ToUpper().Contains("SINGLE"))
            {
                btn.onClick.Invoke();
                Debug.Log("[Test] Clicked: " + txt);
                return;
            }
        }
        Debug.LogWarning("[Test] Single player button not found");
    }
}
