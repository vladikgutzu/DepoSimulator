using UnityEngine;

/// Drives the WeaponArms animation layer, which currently exists for one thing: the attack clip.
///
/// Same shape as PlayerCarryLayer, which does this for a carried box, and for the same reason —
/// a masked layer is how you take one part of the body away from locomotion without taking the
/// locomotion away from the rest of him. The legs keep running, the torso keeps leaning, and the
/// arms play their own pose.
///
/// MeleeSwingArm still works on top: its springs are applied in LateUpdate, after the animator,
/// so a swing reads as a swing out of the carry pose rather than out of a run cycle. What it stops
/// doing while this layer is up is its own two-handed ready stance — that pose now comes from the
/// clip, and both applying it would double the lift.
public class PlayerWeaponLayer : MonoBehaviour
{
    public const string LayerName = "WeaponArms";

    [Tooltip("How briskly the arms take up the weapon pose and let go of it again.")]
    [SerializeField] float _blendSpeed = 8f;

    Animator      _anim;
    MeleeSwingArm _swingArm;
    int           _layer = -1;

    void Awake() => _anim = GetComponentInChildren<Animator>();

    void Start()
    {
        if (_anim == null) return;

        _layer = _anim.GetLayerIndex(LayerName);
        if (_layer >= 0) _anim.SetLayerWeight(_layer, 0f);
        else
            Debug.LogWarning($"[PlayerWeaponLayer] No '{LayerName}' layer on " +
                             $"{_anim.runtimeAnimatorController?.name} — run " +
                             "Tools/Warehouse/Build Weapon Arms Layer.");
    }

    void Update()
    {
        if (_anim == null || _layer < 0) return;

        // The truck and the cart swap the controller out from under him; a layer index from the
        // old one indexes into nothing. Same guard PlayerCarryLayer carries.
        if (_layer >= _anim.layerCount) return;

        var combat = MeleeCombat.Instance;
        bool armed  = combat != null && combat.CurrentWeapon != MeleeWeaponKind.Fists;

        // Up for the blow only. There is no carry animation yet, and an override layer held up
        // with nothing in its state does not leave the arms alone — it keeps feeding back whatever
        // that layer last wrote, which measured 61° of arm lift out of a state playing no clip at
        // all. Down between swings, the arms belong to locomotion and the hand IK, which is what
        // carrying a bat is supposed to look like.
        float target  = armed && combat.IsSwinging ? 1f : 0f;
        float current = _anim.GetLayerWeight(_layer);

        if (!Mathf.Approximately(current, target))
        {
            current = Mathf.MoveTowards(current, target, Time.deltaTime * _blendSpeed);
            _anim.SetLayerWeight(_layer, current);
        }

        // The springs' own two-handed ready stance stays off for the whole time a weapon is held,
        // not just during the blow: it was a stand-in for a carry animation, and standing with the
        // bat raised across the chest is not what is wanted while none exists.
        if (_swingArm == null) _swingArm = GetComponent<MeleeSwingArm>();
        if (_swingArm != null) _swingArm.AnimatorHoldsCarryPose = armed;
    }
}
