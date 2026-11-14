using StarterAssets;
using UnityEngine;

/// Reverses the controls while the player's head is off.
///
/// A robot with no head is not blind — the camera is not on the head, and pretending otherwise
/// would mean playing a black screen, which is not a penalty, it is a pause. What it has lost is
/// the thing that tells left from right: the head is where the sense of direction lived, and
/// without it every instruction the body gets arrives mirrored. Left is right, forward is back, up
/// is down.
///
/// It is the loudest debuff in the game and deliberately so. Losing a leg costs speed and losing an
/// arm costs carrying, both of which a player absorbs and works around; losing a head costs the one
/// thing he cannot delegate to a team-mate, and the fix — find the head, find a toolkit, stand
/// still for six seconds — has to be worth doing rather than worth ignoring.
///
/// **Nothing here reads input.** It sets two flags on StarterAssetsInputs, which inverts at the
/// single point every source funnels through. That is what keeps it compatible with the three
/// components that already write into that vector — HopMovement feeding a hop, PlayerSlipController
/// and RepairCrouch zeroing it — none of which need to know this exists: they all read the vector
/// back as the player's intent, and by then it already is.
[DisallowMultipleComponent]
public class ScrambledControls : MonoBehaviour
{
    [Header("What gets mirrored")]
    [Tooltip("Movement: left is right, forward is back. The half that makes a headless robot walk " +
             "into the shelving it was trying to walk around.")]
    [SerializeField] bool _mirrorMovement = true;

    [Tooltip("Looking: turning and pitch, both. Off if the pair together proves too much to steer " +
             "at all — the movement half alone is already the joke.")]
    [SerializeField] bool _mirrorLook = true;

    StarterAssetsInputs _input;
    bool _scrambled;

    void Awake() => _input = GetComponent<StarterAssetsInputs>();

    void Update()
    {
        // Only a head that has actually come off. A knock to it is a dent — the plating goes and
        // the debuff is the concussion shake — and reversing the controls for a bruise would make
        // the lightest injury in the game the worst one.
        var injuries = InjurySystem.Instance;
        bool wanted = injuries != null && injuries.IsSevered(BodyPart.Head);

        if (wanted == _scrambled) return;

        _scrambled = wanted;
        Apply();
        Announce();
    }

    /// Put back on the way out, whatever the way out was.
    ///
    /// A respawn destroys the player and with it this component, but a body swap and a disabled
    /// rig do not — and a flag left set on an input that outlives this is a player steering
    /// backwards with a head firmly attached, with nothing on screen to explain it.
    void OnDisable()
    {
        _scrambled = false;
        Apply();
    }

    void Apply()
    {
        if (_input == null) return;

        _input.InvertMove = _scrambled && _mirrorMovement;
        _input.InvertLook = _scrambled && _mirrorLook;
    }

    /// Says it out loud, because a player whose controls have just reversed reads it as the game
    /// having broken rather than as something that happened to him.
    void Announce()
    {
        if (_scrambled)
            HUDController.Instance?.AddNotification(
                "No head, no bearings",
                "Your controls are mirrored until you get it back on",
                HUDController.NotifType.Danger);
        else
            HUDController.Instance?.AddNotification(
                "Bearings back",
                "Controls are the right way round again",
                HUDController.NotifType.Success);
    }
}
