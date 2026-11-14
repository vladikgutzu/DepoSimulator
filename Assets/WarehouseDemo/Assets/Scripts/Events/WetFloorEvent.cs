using System.Collections;
using StarterAssets;
using UnityEngine;

/// Wet floor — reduces every player's speed and lowers the ragdoll threshold, and
/// occasionally trips the local player (Tripping -> StandingUp animation, movement locked)
/// while it's active. Lasts 60 seconds.
public class WetFloorEvent : IGameEvent
{
    public string Name        => "WetFloor";
    public string Description => "Spilled liquid: -30% speed, easier to fall";

    const float Duration         = 60f;
    const float SpeedMult        = 0.70f;
    const float RagdollThreshold = 0.55f; // threshold * this = easier ragdoll

    const float SlipCheckIntervalMin  = 4f;
    const float SlipCheckIntervalMax  = 7f;
    const float SlipChance            = 0.35f; // rolled each check, only while moving
    const float SlipChanceSprintCarry = 0.70f; // sprinting while carrying a box is riskier

    MonoBehaviour _host;
    Coroutine     _timer;
    Coroutine     _slipLoop;

    public void Apply(MonoBehaviour host)
    {
        _host = host;
        GameEventManager.EnvironmentSpeedMult    = SpeedMult;
        GameEventManager.RagdollThresholdMult    = RagdollThreshold;

        HUDController.Instance?.AddNotification(
            "WET FLOOR",
            "Liquid spilled — careful! Speed −30%, easy to fall",
            HUDController.NotifType.Danger);

        _timer    = host.StartCoroutine(Timer());
        _slipLoop = host.StartCoroutine(SlipChanceLoop());
    }

    public void Remove()
    {
        GameEventManager.EnvironmentSpeedMult = 1f;
        GameEventManager.RagdollThresholdMult = 1f;

        if (_host != null)
        {
            if (_timer    != null) _host.StopCoroutine(_timer);
            if (_slipLoop != null) _host.StopCoroutine(_slipLoop);
        }

        GameEventManager.Instance?.MarkEventDone(Name);

        HUDController.Instance?.AddNotification(
            "Floor cleaned",
            "The slippery spot is gone — you can move normally",
            HUDController.NotifType.Success);
    }

    IEnumerator Timer()
    {
        yield return new WaitForSeconds(Duration);
        Remove();
    }

    IEnumerator SlipChanceLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(Random.Range(SlipCheckIntervalMin, SlipCheckIntervalMax));

            var slip = PlayerSlipController.Instance;
            if (slip == null || slip.IsSlipping || !slip.IsGrounded) continue;

            var inputs = slip.GetComponent<StarterAssetsInputs>();
            if (inputs == null || inputs.move.sqrMagnitude < 0.01f) continue; // only slip while moving

            bool sprintingWithBox = inputs.sprint && BoxPhysicsInteractable.GrabbedBox != null;
            float chance = sprintingWithBox ? SlipChanceSprintCarry : SlipChance;

            if (Random.value < chance)
                slip.TriggerSlip();
        }
    }
}
