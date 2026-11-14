using UnityEngine;

/// Makes a gate audible from where it actually is.
///
/// This is the whole point of the breach event and it was the part that did not work. The twenty
/// seconds of hammering exist so the team can hear it coming and go and meet it — but the blows
/// were going through AudioManager, which plays one 2D source: the same loudness from nowhere in
/// particular, on a warehouse with five gates a raid can come through. There was no way to tell
/// which one, which is the only question the warning is asking you to answer. (It was also playing
/// the UI error beep.)
///
/// So: real AudioSources on the gate, rolled off with distance, with the sounds a steel shutter
/// makes. Created at runtime — nothing is added to the scene, and a gate nobody attacks costs
/// nothing.
[DisallowMultipleComponent]
public class GateAudio : MonoBehaviour
{
    /// Full volume within this, then falling away.
    ///
    /// Max is past the width of the building on purpose. The floor measures 85 x 110 m and the two
    /// furthest gates a raid can come through are 73 m apart, so at the 60 m this started on a
    /// player at the wrong end of the warehouse heard nothing at all — not faint, silent. A warning
    /// nobody can hear is not a warning. What matters is that it carries *unevenly*, so walking
    /// towards it gets louder.
    const float MinDistance = 4f;
    const float MaxDistance = 100f;

    /// The shutter is a big flat thing; the sound comes off its middle rather than off the pivot,
    /// which sits up at the roller about four and a half metres off the floor.
    const float SourceDrop = 1.4f;

    AudioSource _oneShots;
    AudioSource _strain;
    int         _variant;

    /// Fits a gate with sound, or hands back what it already has.
    public static GateAudio Ensure(GateAnimator gate)
    {
        if (gate == null) return null;

        var audio = gate.GetComponent<GateAudio>();
        return audio != null ? audio : gate.gameObject.AddComponent<GateAudio>();
    }

    void Awake()
    {
        // A child, so the sound sits at door height instead of up at the roller — and so the gate
        // rising does not carry the source three metres into the ceiling with it.
        var holder = new GameObject("GateAudio");
        holder.transform.SetParent(transform, false);
        holder.transform.localPosition = Vector3.down * SourceDrop;

        _oneShots = Configure(holder.AddComponent<AudioSource>());

        _strain = Configure(holder.AddComponent<AudioSource>());
        _strain.clip        = GateSfx.Rattle;
        _strain.loop        = true;
        _strain.volume      = 0f;
        _strain.pitch       = 0.9f;
    }

    static AudioSource Configure(AudioSource source)
    {
        source.playOnAwake  = false;
        source.spatialBlend = 1f;                       // fully positional, the entire point
        source.rolloffMode  = AudioRolloffMode.Logarithmic;
        source.minDistance  = MinDistance;
        source.maxDistance  = MaxDistance;
        source.dopplerLevel = 0f;                       // nothing here moves fast enough to warrant it
        return source;
    }

    /// One blow on the shutter. `force` runs 0 to 1 over the course of the beating.
    ///
    /// Escalates with it: louder, and pitched down as it goes. A gate that sounds heavier the
    /// longer they work on it is the same information the cracks carry, for anyone facing the
    /// other way.
    public void Impact(float force)
    {
        if (_oneShots == null) return;

        force = Mathf.Clamp01(force);

        // Cycled rather than rolled, so the same clip never lands twice running.
        _variant = (_variant + 1) % 3;

        _oneShots.pitch  = Mathf.Lerp(1.06f, 0.88f, force) + Random.Range(-0.04f, 0.04f);
        _oneShots.PlayOneShot(GateSfx.Impact(_variant), Mathf.Lerp(0.55f, 1f, force));
    }

    /// The continuous strain under the blows. 0 is silence, 1 is a crowd leaning on it.
    public void SetStrain(float amount)
    {
        if (_strain == null) return;

        amount = Mathf.Clamp01(amount);

        if (amount > 0.001f && !_strain.isPlaying) _strain.Play();
        else if (amount <= 0.001f && _strain.isPlaying) _strain.Stop();

        _strain.volume = amount * 0.35f;
    }

    /// The shutter travelling, either way.
    public void Motor()
    {
        if (_oneShots == null) return;

        // Quieter than the number alone suggests it should be. The roller is a sustained sound and
        // measures four times the impacts' RMS at the same volume, so matching them by ear means
        // not matching them by setting — otherwise the gate opening drowns out the beating that
        // earned it.
        _oneShots.pitch = 1f;
        _oneShots.PlayOneShot(GateSfx.Roller, 0.32f);
    }
}
