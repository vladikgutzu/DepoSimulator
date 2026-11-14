using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

/// Photographs every weapon in the player's hand so the placements can actually be judged.
///
/// The numbers in a weapon asset say nothing about whether the thing looks held. This equips
/// each one in turn, freezes the clock and takes a close-up of the fist from a fixed angle, so
/// six weapons can be compared against each other rather than each remembered from a separate
/// play session.
///
/// Pass {"swing": true} to catch them mid-strike instead of at rest.
public static class WeaponPoseCheck
{
    const string Folder = "Screenshots/WeaponPoses";

    public static string Execute() => Run(false);
    public static string ExecuteSwing() => Run(true);

    static string Run(bool swing)
    {
        if (!Application.isPlaying) return "play mode only";
        if (UnityEngine.Object.FindAnyObjectByType<Session>() != null) return "already running";

        Directory.CreateDirectory(Folder);

        var go = new GameObject("~WeaponPoseCheck");
        go.AddComponent<Session>().Swing = swing;

        return $"capturing {(swing ? "swings" : "rest poses")} - about 30 seconds";
    }

    class Session : MonoBehaviour
    {
        public bool Swing;

        Camera _photo, _gameCam;

        IEnumerator Start()
        {
            DontDestroyOnLoad(gameObject);
            yield return new WaitForSecondsRealtime(0.6f);

            if (MainMenuController.Instance != null)
            {
                var doc = MainMenuController.Instance.GetComponent<UnityEngine.UIElements.UIDocument>();
                if (doc != null) doc.enabled = false;
            }

            // Not MeleeCombat.Instance: compiling a script during play reloads the domain and
            // clears statics, so the singleton is often null in a session that has been tinkered
            // with while running - which is exactly when this tool gets used.
            var combat = UnityEngine.Object.FindAnyObjectByType<MeleeCombat>();
            if (combat == null) { Finish("no MeleeCombat"); yield break; }

            var anim = combat.GetComponentInChildren<Animator>();
            var hand = anim != null ? anim.GetBoneTransform(HumanBodyBones.RightHand) : null;
            if (hand == null) { Finish("no right hand bone"); yield break; }

            BuildCamera();

            var kinds = Enum.GetValues(typeof(MeleeWeaponKind))
                            .Cast<MeleeWeaponKind>()
                            .Where(k => k != MeleeWeaponKind.Fists)
                            .ToList();

            foreach (var kind in kinds)
            {
                Time.timeScale = 1f;
                combat.EquipWeapon(kind, MeleeWeaponItem.BuildVisual(kind));

                // The arm springs into its carry pose over about a second.
                yield return new WaitForSecondsRealtime(1.4f);

                if (Swing)
                {
                    var arm = combat.GetComponent<MeleeSwingArm>();
                    if (arm != null)
                    {
                        arm.Begin(0.35f, 0.2f, 0.45f);
                        // Caught just after the blow starts travelling, which is the frame that
                        // shows whether the grip reads during the swing rather than only at rest.
                        yield return new WaitForSecondsRealtime(0.42f);
                    }
                }

                Time.timeScale = 0f;
                yield return null;

                // Framed on the hand from the player's front-right, close enough that the grip
                // fills the frame and far enough to keep the weapon's whole length in it.
                Frame(hand.position, 1.5f, combat.transform);
                yield return null;
                yield return null;

                string path = Path.Combine(Folder, $"{(Swing ? "swing" : "rest")}_{kind}.png");
                ScreenCapture.CaptureScreenshot(path, 2);

                yield return new WaitForSecondsRealtime(1.2f);
                Debug.Log($"[Pose] {path}");
            }

            Finish("done");
        }

        void BuildCamera()
        {
            _gameCam = Camera.main;

            var go = new GameObject("~PoseCamera");
            _photo = go.AddComponent<Camera>();

            if (_gameCam != null) { _photo.CopyFrom(_gameCam); _gameCam.enabled = false; }

            _photo.fieldOfView = 40f;
            _photo.depth       = 100f;
            _photo.nearClipPlane = 0.05f;
        }

        /// Looks at the hand from in front of the player and slightly to his right, so the palm
        /// and the shaft are both visible instead of one hiding the other.
        void Frame(Vector3 subject, float distance, Transform player)
        {
            Vector3 dir = (player.forward * 0.75f + player.right * 0.55f + Vector3.up * 0.2f).normalized;

            _photo.transform.position = subject + dir * distance;
            _photo.transform.rotation = Quaternion.LookRotation(subject - _photo.transform.position);
        }

        void Finish(string msg)
        {
            Time.timeScale = 1f;
            if (_gameCam != null) _gameCam.enabled = true;
            if (_photo != null) Destroy(_photo.gameObject);

            Debug.Log("[Pose] " + msg);
            Destroy(gameObject);
        }
    }
}
