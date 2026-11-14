# Quick Design Spec: Carried Ladder Collider

**Type**: Tweak
**System**: Portable ladder carrying
**GDD Reference**: No GDD found
**Date**: 2026-09-24

**Outcome**: Rejected after playtest. The enabled kinematic collider moved the
follow camera closer but did not stop CharacterController movement through
shelving. Superseded by `carried-ladder-clearance-tweak-2026-09-24.md`.

## Change Summary

Keep the ladder's primary solid collider enabled while it is carried, both for
the local carrier and remote visual copies. The ladder rigidbody remains
kinematic with gravity disabled during carrying.

## Motivation

Disabling the collider makes only the player's CharacterController participate
in movement collisions. The six-metre ladder visual can consequently pass
through shelves and walls whenever the player's smaller capsule fits through.

## Design Delta

The carried ladder previously disabled its collider until placement.

This experiment keeps that collider enabled throughout the Carried state so
warehouse geometry can register contact with the ladder volume.

## New Rules / Values

- A carried ladder retains its authored solid BoxCollider.
- Its Rigidbody remains kinematic and does not use gravity while carried.
- Local and remote carried copies use the same collider state.
- Placement, leaning, and climbing collider behaviour remains unchanged.

## Affected Systems

| System | Impact | Action Required |
|--------|--------|-----------------|
| Ladder carrying | Carried volume participates in collisions | Keep the collider enabled in both carry paths |
| Physics props | May receive contact from the carried ladder | Validate boxes and pallets during playtest |
| Multiplayer visualization | Remote carried ladder uses matching collision state | Validate host and client copies |

## Acceptance Criteria

- [ ] A carried ladder no longer passes visibly through shelving without contact.
- [ ] Picking up the ladder does not displace or trap the carrier.
- [ ] Carrying beside walls does not produce persistent jitter.
- [ ] Boxes and pallets are not launched by ordinary ladder movement.
- [ ] Placing and climbing the ladder still work.
- [ ] Local and remote carry behaviour remains consistent.

## GDD Update Required?

No. No project GDD or systems index is present; this experimental tweak is
fully described by the quick spec.
