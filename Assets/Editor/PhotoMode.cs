using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using UnityEditor;

/// A photo mode for listing shots, and a set of staged scenes to point it at.
///
/// Built because shooting the live game does not work: the third-person camera's yaw lives in
/// private input state, so a frame cannot be aimed from script, and a raid brings its own
/// events with it — the last attempt lost three shots out of four to a saboteur cutting the
/// power mid-session. So this takes the camera away from Cinemachine entirely, freezes the
/// clock, and builds each picture on purpose.
///
/// The staged characters are stripped clones of the player: no controllers, no networking,
/// their animators parked on a chosen pose. Nothing here starts a multiplayer session — a
/// second worker in frame is a prop, not a peer.
public static class PhotoMode
{
    const string Folder = "Screenshots";

    [MenuItem("Tools/Warehouse/Photo Session", priority = 90)]
    public static void Run() => Debug.Log(Execute());

    public static string Execute()
    {
        if (!Application.isPlaying) return "play mode only — the Game view is what gets captured";
        if (Object.FindAnyObjectByType<Session>() != null) return "a session is already running";

        Directory.CreateDirectory(Folder);
        new GameObject("~PhotoSession").AddComponent<Session>();

        return "photo session started — five scenes, about a minute";
    }

    // ══ The session ═════════════════════════════════════════════════════════

    class Session : MonoBehaviour
    {
        readonly List<GameObject> _props  = new();   // everything, for cleanup
        readonly List<GameObject> _staged = new();   // just this scene's subjects

        Camera    _photo;
        Camera    _gameCam;
        Transform _player;
        int       _shot;

        IEnumerator Start()
        {
            DontDestroyOnLoad(gameObject);
            yield return new WaitForSecondsRealtime(0.5f);

            HideMenu();

            var playerGo = GameObject.FindGameObjectWithTag("Player");
            if (playerGo == null) { Debug.LogWarning("[Photo] no player in the scene"); Destroy(gameObject); yield break; }
            _player = playerGo.transform;

            BuildCamera();

            // Everything is posed by hand from here, so nothing may move on its own: no events
            // firing, no raiders pathing, no lights going out halfway through a shot.
            Time.timeScale = 0f;

            yield return Scene_ForkliftCarryingPallet();
            yield return Scene_CarryingParts();
            yield return Scene_ForkliftAndLadder();
            yield return Scene_ZombieFleeing();
            yield return Scene_ZombieChasingTwo();

            Cleanup();
            Debug.Log($"[Photo] done — {_shot} shots in {Folder}/");
            Destroy(gameObject);
        }

        // ── Scenes ──────────────────────────────────────────────────────────

        /// A reach truck with a loaded pallet on its forks and a driver aboard.
        IEnumerator Scene_ForkliftCarryingPallet()
        {
            _staged.Clear();

            var truck = GameObject.Find("Reach_Truck_01a_Rig_Prefab_01");
            if (truck == null) { Debug.LogWarning("[Photo] no reach truck"); yield break; }

            var mount = truck.transform.Find("Reach_Truck_01a_Rig/Root/Body/Front_01/ForkMount")
                     ?? truck.transform;

            // A loaded pallet lifted onto the forks. Borrowing a real one keeps its boxes and
            // its materials rather than building a stand-in that looks like neither.
            // The boxes are loose rigidbodies that only happen to be sitting on the pallet, so
            // moving the zone on its own drops them in a heap where the pallet used to be. They
            // have to travel with it, pinned so physics does not immediately spill them again.
            var pallet = NearestLoadedPallet(truck.transform.position);
            if (pallet != null)
                MovePalletWithLoad(pallet, mount.position + Vector3.up * 0.12f,
                                   truck.transform.rotation);

            SeatDriver(truck.transform);

            // The truck and its load are the subject as much as the driver is.
            _staged.Add(truck);
            if (pallet != null) _staged.Add(pallet.gameObject);

            yield return Breathe();
            yield return ShootProps("forklift_pallet", yaw: 35f, pitch: 12f, pad: 4f);
        }

        /// Three workers walking a line with boxes in their arms.
        IEnumerator Scene_CarryingParts()
        {
            _staged.Clear();

            Vector3 origin = _player.position + _player.forward * 3f;

            for (int i = 0; i < 3; i++)
            {
                Vector3 pos = origin + _player.right * (i * 1.6f - 1.6f) + _player.forward * (i * 0.7f);
                var w = SpawnWorker(pos, Quaternion.LookRotation(-_player.forward), speed: 2f);
                GiveBox(w);
            }

            yield return Breathe();
            yield return ShootProps("carrying_parts", yaw: 200f, pitch: 10f);
        }

        /// One aboard the truck, one up the ladder — the two jobs side by side.
        IEnumerator Scene_ForkliftAndLadder()
        {
            _staged.Clear();

            var ladder = GameObject.Find("Ladder/Ladder") ?? GameObject.Find("Ladder");
            var truck  = GameObject.Find("Reach_Truck_01a_Rig_Prefab_01");
            if (ladder == null) { Debug.LogWarning("[Photo] no ladder"); yield break; }

            // The truck is brought alongside the ladder so both fit one frame.
            if (truck != null)
            {
                Vector3 beside = ladder.transform.position + ladder.transform.right * 3.2f;
                beside.y = truck.transform.position.y;
                truck.transform.position = beside;
                truck.transform.rotation = Quaternion.LookRotation(-ladder.transform.right);

                SpawnWorker(truck.transform.TransformPoint(new Vector3(0f, 0.55f, -0.15f)),
                            truck.transform.rotation, speed: 0f);
            }

            // Part way up, facing the rungs.
            SpawnWorker(ladder.transform.position + Vector3.up * 1.7f + ladder.transform.forward * 0.25f,
                        Quaternion.LookRotation(-ladder.transform.forward), speed: 0f);

            _staged.Add(ladder);
            if (truck != null) _staged.Add(truck);

            yield return Breathe();
            yield return ShootProps("forklift_and_ladder", yaw: 55f, pitch: 8f, pad: 4f);
        }

        /// A raider caught mid-stride running away from the player.
        IEnumerator Scene_ZombieFleeing()
        {
            _staged.Clear();

            Vector3 stand = _player.position;
            Vector3 away  = _player.forward;

            var zombie = SpawnZombie(stand + away * 5.5f, Quaternion.LookRotation(away), speed: 1.6f);
            if (zombie == null) yield break;

            // The player belongs in frame too - the shot is him watching it run.
            _staged.Add(_player.gameObject);

            yield return Breathe();
            yield return ShootProps("zombie_fleeing", yaw: 165f, pitch: 8f);
        }

        /// The other way round: two workers backing off while a raider comes at them.
        IEnumerator Scene_ZombieChasingTwo()
        {
            _staged.Clear();

            Vector3 stand = _player.position;
            Vector3 away  = _player.forward;

            SpawnWorker(stand + _player.right * 1.4f, Quaternion.LookRotation(away), speed: 2.6f);
            SpawnWorker(stand - _player.right * 1.2f + away * 0.6f,
                        Quaternion.LookRotation(away), speed: 2.6f);

            SpawnZombie(stand + away * 4f, Quaternion.LookRotation(-away), speed: 1.6f, phase: 0.62f);

            yield return Breathe();
            yield return ShootProps("zombie_chasing", yaw: 60f, pitch: 10f);
        }

        // ── Camera ──────────────────────────────────────────────────────────

        void BuildCamera()
        {
            _gameCam = Camera.main;

            var go = new GameObject("~PhotoCamera");
            _props.Add(go);

            _photo = go.AddComponent<Camera>();
            if (_gameCam != null)
            {
                // Copy first so post-processing, layer mask and clip planes match the game's
                // look; a shot lit differently from the game is not a shot of the game.
                _photo.CopyFrom(_gameCam);
                _gameCam.enabled = false;
            }

            _photo.fieldOfView = 55f;
            _photo.depth       = 100f;
        }

        /// Places the camera on an orbit around a point and looks at it.
        void Frame(Vector3 subject, float distance, float yaw, float pitch)
        {
            var rot = Quaternion.Euler(pitch, yaw, 0f);
            _photo.transform.position = subject + rot * new Vector3(0f, 0f, -distance);
            _photo.transform.rotation = Quaternion.LookRotation(subject - _photo.transform.position);
        }

        /// Frames whatever the current scene actually put on the floor, rather than a point
        /// worked out in advance. Guessing the subject is what left three staged workers just
        /// outside the frame twice running: the props know where they are, so let them say.
        IEnumerator ShootProps(string name, float yaw, float pitch, float pad = 3.2f)
        {
            var live = _staged.Where(p => p != null).ToList();
            if (live.Count == 0) { Debug.LogWarning($"[Photo] {name}: nothing staged"); yield break; }

            var bounds = new Bounds(live[0].transform.position, Vector3.one * 0.5f);
            foreach (var p in live) bounds.Encapsulate(p.transform.position);

            // Back off far enough that the whole spread fits, with a floor for a lone subject.
            float distance = Mathf.Max(5f, bounds.extents.magnitude * 2.2f + pad);
            Vector3 subject = bounds.center + Vector3.up * 1.0f;

            yaw = ClearYaw(subject, distance, yaw, pitch);

            yield return Shoot(name, subject, distance, yaw, pitch);
        }

        /// Walks the orbit until the subject is actually visible. A warehouse is full of racking
        /// and structural columns, and the best-composed angle is worthless if a pillar is
        /// standing in it - which is exactly what happened to the first fleeing-zombie shot.
        float ClearYaw(Vector3 subject, float distance, float yaw, float pitch)
        {
            for (int step = 0; step < 12; step++)
            {
                float candidate = yaw + step * 30f;

                var rot = Quaternion.Euler(pitch, candidate, 0f);
                Vector3 eye = subject + rot * new Vector3(0f, 0f, -distance);

                // Triggers ignored: shelf trigger volumes and interaction zones are not things
                // the camera can actually see.
                if (!Physics.Linecast(eye, subject, ~0, QueryTriggerInteraction.Ignore))
                    return candidate;
            }

            Debug.Log("[Photo] no clear angle found - keeping the requested one");
            return yaw;
        }

        IEnumerator Shoot(string name, Vector3 subject, float distance, float yaw, float pitch)
        {
            Frame(subject, distance, yaw, pitch);
            Debug.Log($"[Photo] {name}: subject {subject}, cam {_photo.transform.position}");

            // Two frames so the camera's new position is actually rendered before the capture
            // request is honoured.
            yield return null;
            yield return null;

            string path = Path.Combine(Folder, $"{++_shot:00}_{name}.png");
            ScreenCapture.CaptureScreenshot(path, 2);

            yield return new WaitForSecondsRealtime(1.5f);
            Debug.Log($"[Photo] {path}");
        }

        /// Time is stopped, so animators need a moment of real time to settle into their pose
        /// before the shutter. Unscaled so it works with timeScale at zero.
        IEnumerator Breathe()
        {
            Time.timeScale = 1f;
            yield return new WaitForSecondsRealtime(0.6f);
            Time.timeScale = 0f;
            yield return null;
        }

        // ── Props ───────────────────────────────────────────────────────────

        /// A clone of the player with everything that makes him a player taken out: no input,
        /// no networking, no physics of his own. Purely something to photograph.
        GameObject SpawnWorker(Vector3 pos, Quaternion rot, float speed)
        {
            // The cached player, not a fresh tag lookup: the first clone has its tag cleared so
            // that nothing else mistakes it for the real one, and a lookup that lands on a clone
            // - or on nothing - is how three staged workers silently failed to appear.
            var src = _player != null ? _player.gameObject : null;
            if (src == null) { Debug.LogWarning("[Photo] no player to clone"); return null; }

            // Cloned while the source is switched off, so the copy comes into the world inactive
            // and none of its Awake methods run. That matters: the player carries singletons that
            // answer a duplicate with Destroy(gameObject), so a clone made the ordinary way
            // deletes itself the moment it wakes - which is why staged workers kept vanishing
            // without so much as a warning.
            bool wasActive = src.activeSelf;
            src.SetActive(false);

            var clone = Object.Instantiate(src, pos, rot);

            src.SetActive(wasActive);

            clone.name = "~Worker";
            clone.tag  = "Untagged";     // or every lookup for "the player" finds him instead
            _props.Add(clone);

            // Safe to destroy outright now: nothing has woken, so nothing has an OnDestroy to
            // run on the way out.
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null) Object.DestroyImmediate(mb);
            foreach (var c in clone.GetComponentsInChildren<CharacterController>(true))
                if (c != null) Object.DestroyImmediate(c);

            clone.SetActive(true);

            var anim = clone.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                // No controller is writing these any more, so whatever is set here is the pose
                // the blend tree settles on.
                anim.applyRootMotion = false;
                anim.cullingMode     = AnimatorCullingMode.AlwaysAnimate;
                anim.SetFloat("Speed", speed);
                anim.SetFloat("MotionSpeed", 1f);
                anim.SetBool("Grounded", true);
            }

            _staged.Add(clone);
            return clone;
        }

        /// Puts a box in a worker's hands so he reads as carrying something rather than walking
        /// about empty-handed.
        void GiveBox(GameObject worker)
        {
            if (worker == null) return;

            var anim = worker.GetComponentInChildren<Animator>();
            var hand = anim != null && anim.isHuman
                ? anim.GetBoneTransform(HumanBodyBones.Chest)
                : worker.transform;

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "~CarriedBox";
            Object.Destroy(box.GetComponent<Collider>());

            box.transform.SetParent(hand, false);
            box.transform.localPosition = new Vector3(0f, 0.05f, 0.42f);
            box.transform.localScale    = new Vector3(0.45f, 0.35f, 0.4f);

            var r = box.GetComponent<Renderer>();
            if (r != null)
            {
                var c = new Color(0.66f, 0.5f, 0.32f);
                r.material.color = c;
                r.material.SetColor("_BaseColor", c);
            }
        }

        /// A raider posed for the camera: the FSM and the agent are off, so he holds the stride
        /// he is put in instead of walking out of frame.
        GameObject SpawnZombie(Vector3 pos, Quaternion rot, float speed, float phase = 0.3f)
        {
            if (ThiefController.Prefab == null) { Debug.LogWarning("[Photo] no thief prefab"); return null; }

            var z = Object.Instantiate(ThiefController.Prefab, pos, rot);
            z.name = "~Raider";
            _props.Add(z);

            foreach (var mb in z.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null) mb.enabled = false;
            foreach (var a in z.GetComponentsInChildren<NavMeshAgent>(true))
                if (a != null) a.enabled = false;

            var anim = z.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                // The prefab culls transform writes when off-screen, and the camera is not
                // framed on him until after the clock stops - so he was being left in whatever
                // pose he happened to load in, which is why he never looked like he was running.
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                anim.SetFloat("Speed", speed);
                anim.SetFloat("MotionSpeed", 1f);
                anim.SetBool("Grounded", true);

                // Parked on a deliberate frame rather than wherever a fraction of a second of
                // real time happens to land: 0.3 of the cycle is mid-stride, one leg driving
                // and the other trailing, which is what reads as running in a still.
                int locomotion = Animator.StringToHash("Locomotion");
                if (anim.HasState(0, locomotion))
                {
                    anim.Play(locomotion, 0, phase);
                    anim.Update(0f);          // evaluate now; the clock is stopped
                }
            }

            _staged.Add(z);
            return z;
        }

        /// Moves a loaded pallet and everything stacked on it as one piece.
        void MovePalletWithLoad(PalletStackZone pallet, Vector3 pos, Quaternion rot)
        {
            var boxes = pallet.BoxesOnPallet.Where(b => b != null).ToList();
            var keep  = boxes.Select(b => pallet.transform.InverseTransformPoint(b.transform.position))
                             .ToList();
            var keepR = boxes.Select(b => Quaternion.Inverse(pallet.transform.rotation) * b.transform.rotation)
                             .ToList();

            pallet.transform.SetPositionAndRotation(pos, rot);

            for (int i = 0; i < boxes.Count; i++)
            {
                var rb = boxes[i].GetComponent<Rigidbody>();
                if (rb != null) rb.isKinematic = true;   // riding the forks, not falling off them

                boxes[i].transform.SetPositionAndRotation(
                    pallet.transform.TransformPoint(keep[i]), rot * keepR[i]);
            }
        }

        /// Sits a worker on the truck if it has something that looks like a seat, and leaves him
        /// out of the frame entirely if it does not — a driver standing beside his own forklift
        /// is worse than no driver.
        void SeatDriver(Transform truck)
        {
            Transform seat = null;
            foreach (var t in truck.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("seat") || n.Contains("driver") || n.Contains("operator")) { seat = t; break; }
            }

            if (seat == null) { Debug.Log("[Photo] no seat on the truck - driver left out"); return; }

            SpawnWorker(seat.position + Vector3.up * 0.05f, truck.rotation, speed: 0f);
        }

        PalletStackZone NearestLoadedPallet(Vector3 near)
        {
            PalletStackZone best = null;
            float bestSqr = float.MaxValue;

            foreach (var z in PalletStackZone.All)
            {
                if (z == null || z.BoxesOnPallet == null || z.BoxesOnPallet.Count == 0) continue;

                float sqr = (z.transform.position - near).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                bestSqr = sqr; best = z;
            }

            return best;
        }

        static void HideMenu()
        {
            if (MainMenuController.Instance == null) return;

            var doc = MainMenuController.Instance.GetComponent<UnityEngine.UIElements.UIDocument>();
            if (doc != null) doc.enabled = false;
            else MainMenuController.Instance.gameObject.SetActive(false);
        }

        void Cleanup()
        {
            Time.timeScale = 1f;

            foreach (var p in _props) if (p != null) Object.Destroy(p);
            if (_gameCam != null) _gameCam.enabled = true;
        }

        void OnDestroy() => Time.timeScale = 1f;
    }
}
