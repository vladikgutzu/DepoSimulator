# Quick Design Spec: Pallet Lift Latch Click

**Type**: Tweak
**System**: Forklift / Pallet Jack pallet pickup
**GDD Reference**: No dedicated pallet-lift GDD found
**Date**: 2026-10-01

## Change Summary

Play one short metallic latch click when a forklift or pallet jack successfully
secures a pallet.

## Motivation

The pallet jack has very little visible fork travel, so the operator cannot
reliably tell whether the pallet was actually captured. A distinct confirmation
sound makes the successful state change immediately readable without adding UI.

## Design Delta

There is no dedicated pallet-lift GDD. The existing pickup rules remain
unchanged. This spec adds feedback to the successful transition only.

## New Rules / Values

- Play one short metallic latch click only after the pallet lift is accepted.
- In multiplayer, the operator hears it after server approval.
- Already-connected observers hear the click spatially at the pallet.
- Applying an already-active lift during late join is silent.
- Pressing lift without a valid pallet, receiving a denied lift, or continuing
  to hold the lift input while a pallet is attached produces no click.
- Forklift and pallet jack use the same sound.

## Affected Systems

| System | Impact | Action Required |
|--------|--------|-----------------|
| Pallet pickup | Adds confirmation feedback | Play sound on confirmed attach |
| Pallet networking | Distinguishes live lift from late-join hydration | Suppress hydration sound |
| Audio | Adds a short procedural 3D latch sound | Generate and cache one clip |

## Acceptance Criteria

- [ ] Forklift plays exactly one click when it captures a pallet.
- [ ] Pallet jack plays exactly one click when it captures a pallet.
- [ ] No click plays when no pallet is found or the server rejects the lift.
- [ ] The click does not repeat while the pallet remains held.
- [ ] Nearby connected players hear a spatial click for a live pickup.
- [ ] A late joiner does not hear a false click for a pallet already being held.

## GDD Update Required?

No. This is feedback for an existing state transition and does not change the
pickup rules.
