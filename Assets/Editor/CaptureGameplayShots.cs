using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;

/// Stages and shoots a set of listing screenshots out of the running game.
///
/// I cannot play the game, but I can put it into the poses worth photographing: force a raid,
/// stand the player where he can see it, arm him, and knock one raider down. Everything is
/// captured from the Game view, so the shots have the real camera, the real lighting and the
/// real HUD in them.
public static class CaptureGameplayShots
{
    const string Folder = "Screenshots";

    public static string Execute()
    {
        if (!Application.isPlaying)
            return "not in play mode — start the game first, the Game view is what gets captured";

        var existing = Object.FindFirstObjectByType<ShotDirector>();
        if (existing != null) return "a capture run is already going";

        Directory.CreateDirectory(Folder);

        var go = new GameObject("~ShotDirector");
        go.AddComponent<ShotDirector>();

        return "capture run started — four shots, roughly 25 seconds";
    }

    /// Lives in the running game for the length of the run, then deletes itself.
    class ShotDirector : MonoBehaviour
    {
        int _shot;

        IEnumerator Start()
        {
            // Entering play mode lands on the main menu, and starting a game reloads the scene.
            // Surviving that load is the only way this can drive what happens after it.
            DontDestroyOnLoad(gameObject);

            yield return new WaitForSeconds(1f);

            // The menu is an overlay drawn on top of an already-running warehouse, not a
            // separate scene: the player, the pallets and the event manager are all live behind
            // it. Hiding it is enough, and far better than pressing Single Player, which reloads
            // the scene into a shift whose pallets are not loaded yet — and a raid on an empty
            // warehouse refuses to start at all, so there would be nothing to photograph.
            if (MainMenuController.Instance != null)
            {
                // Only the document: disabling the whole object can take the HUD with it if
                // they share a GameObject, and a screenshot with no HUD is not a screenshot of
                // the game.
                var doc = MainMenuController.Instance.GetComponent<UnityEngine.UIElements.UIDocument>();
                if (doc != null) doc.enabled = false;
                else MainMenuController.Instance.gameObject.SetActive(false);
                yield return new WaitForSeconds(0.5f);
            }

            // Screenshots of a game are taken with the mouse captured, the way it looks in play.
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible   = false;

            var player = GameObject.FindGameObjectWithTag("Player");
            if (player == null) { Debug.LogWarning("[Shots] no player — is the game scene loaded?"); Destroy(gameObject); yield break; }

            // The first raider of every wave is Aggressive by design, so he walks to the player
            // on his own. Letting him arrive is the only way to get a usable frame: teleporting
            // the player leaves Cinemachine jammed inside his back, and there is no way from
            // here to fly the camera by hand.
            bool started = GameEventManager.Instance != null
                        && GameEventManager.Instance.ForceStartEvent("Thief");

            Debug.Log($"[Shots] raid start = {started}");

            // Armed straight away so every frame from here has the bat in it.
            if (MeleeCombat.Instance != null)
                MeleeCombat.Instance.EquipWeapon(
                    MeleeWeaponKind.Bat, MeleeWeaponItem.BuildVisual(MeleeWeaponKind.Bat));

            // ── 1. First sight of them ────────────────────────────────────────
            yield return WaitForRaiderWithin(player, 14f, timeout: 30f);
            Debug.Log($"[Shots] raiders on the floor = {ThiefController.All.Count}");
            yield return CaptureAfterSettle("raid_incoming", settle: 0f);

            // ── 2. One of them closing in ─────────────────────────────────────
            yield return WaitForRaiderWithin(player, 5f, timeout: 30f);
            yield return CaptureAfterSettle("closing_in", settle: 0f);

            // ── 3. A charged blow throwing the body ───────────────────────────
            var target = Nearest(player);
            if (target != null)
            {
                Vector3 aim = target.transform.position - player.transform.position;
                aim.y = 0f;
                target.TakeHit(3, aim.normalized, heavy: true);

                // Mid-flight rather than after it has settled.
                yield return new WaitForSeconds(0.3f);
                yield return CaptureAfterSettle("charged_knockdown", settle: 0f);
            }

            // ── 4. The next one still coming ──────────────────────────────────
            yield return WaitForRaiderWithin(player, 6f, timeout: 30f);
            yield return CaptureAfterSettle("raid_continues", settle: 0f);

            Debug.Log($"[Shots] done — {_shot} captured into {Folder}/");
            Destroy(gameObject);
        }

        static ThiefController Nearest(GameObject player)
        {
            ThiefController best = null;
            float bestSqr = float.MaxValue;

            foreach (var t in ThiefController.All)
            {
                if (t == null || t.Current == ThiefController.State.Down) continue;

                float sqr = (t.transform.position - player.transform.position).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr; best = t;
            }

            return best;
        }

        /// Blocks until a live raider is inside range, or the timeout runs out — a shot of an
        /// empty aisle is worse than a slightly early one.
        static IEnumerator WaitForRaiderWithin(GameObject player, float range, float timeout)
        {
            float waited = 0f;

            while (waited < timeout)
            {
                var near = Nearest(player);
                if (near != null &&
                    Vector3.Distance(near.transform.position, player.transform.position) <= range)
                    yield break;

                waited += Time.deltaTime;
                yield return null;
            }
        }

        IEnumerator CaptureAfterSettle(string name, float settle = 0.9f)
        {
            // Cinemachine eases toward its target, so a shot taken the same frame the player was
            // moved is of the camera still travelling.
            if (settle > 0f) yield return new WaitForSeconds(settle);

            string path = Path.Combine(Folder, $"{++_shot:00}_{name}.png");
            ScreenCapture.CaptureScreenshot(path, 2);

            // The capture lands on a later frame; give it time before the next one overwrites
            // the request.
            yield return new WaitForSeconds(1.5f);

            Debug.Log($"[Shots] {path}");
        }
    }
}
