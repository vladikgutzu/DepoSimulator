using UnityEngine;
using System.Reflection;

public class DiagTutorial
{
    public static void Execute()
    {
        // Which scene is open
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        Debug.Log($"[DiagTutorial] Active scene: '{scene.name}' path='{scene.path}'");

        // TutorialManager
        var tm = Object.FindAnyObjectByType<TutorialManager>(FindObjectsInactive.Include);
        if (tm == null)
        {
            Debug.LogError("[DiagTutorial] TutorialManager NOT FOUND in scene");
        }
        else
        {
            var fStep = typeof(TutorialManager).GetField("_step", BindingFlags.NonPublic | BindingFlags.Instance);
            var step = fStep?.GetValue(tm);
            Debug.Log($"[DiagTutorial] TutorialManager on '{tm.gameObject.name}', active={tm.gameObject.activeInHierarchy}, enabled={tm.enabled}, _step={step}");
        }

        // GameManager
        var gm = Object.FindAnyObjectByType<GameManager>(FindObjectsInactive.Include);
        if (gm == null)
            Debug.LogError("[DiagTutorial] GameManager NOT FOUND");
        else
            Debug.Log($"[DiagTutorial] GameManager on '{gm.gameObject.name}', active={gm.gameObject.activeInHierarchy}, GameStarted={gm.GameStarted}, ActiveOrder={gm.ActiveOrder?.name ?? "null"}");

        // PartHighlighter
        var ph = Object.FindAnyObjectByType<PartHighlighter>(FindObjectsInactive.Include);
        if (ph == null)
            Debug.LogError("[DiagTutorial] PartHighlighter NOT FOUND in scene");
        else
            Debug.Log($"[DiagTutorial] PartHighlighter on '{ph.gameObject.name}', active={ph.gameObject.activeInHierarchy}, enabled={ph.enabled}");

        // Part boxes (the only place parts live — there is no inventory)
        Debug.Log($"[DiagTutorial] BoxWithPart.All count = {BoxWithPart.All.Count}");

        // RoleController
        var rc = Object.FindAnyObjectByType<RoleController>(FindObjectsInactive.Include);
        if (rc == null)
            Debug.Log("[DiagTutorial] RoleController: null (singleplayer mode — OK)");
        else
            Debug.Log($"[DiagTutorial] RoleController on '{rc.gameObject.name}', CurrentRole={rc.CurrentRole?.name ?? "null"}, HighlightRange={rc.CurrentRole?.PartHighlightRange ?? 0f}");

        // OnOrderAccepted subscribers count
        var fEvent = typeof(GameManager).GetField("OnOrderAccepted", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        if (fEvent != null)
        {
            var del = fEvent.GetValue(gm) as System.Delegate;
            var invList = del?.GetInvocationList();
            Debug.Log($"[DiagTutorial] GameManager.OnOrderAccepted subscribers: {invList?.Length ?? 0}");
            if (invList != null)
                foreach (var d in invList)
                    Debug.Log($"[DiagTutorial]   -> {d.Target?.GetType().Name}.{d.Method.Name}");
        }
    }
}
