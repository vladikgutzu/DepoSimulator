# Quick Design Spec: Flying Box Catch

**Type**: Addition
**System**: Player interaction / box physics
**GDD Reference**: No GDD found
**Date**: 2026-09-23

## Change Summary

Allow a player with empty hands to catch a moving airborne box by aiming near it and pressing the bound interaction control. The catch reuses the existing box grab and multiplayer arbitration paths.

## Motivation

Thrown boxes move too quickly for the existing narrow interaction ray to be a practical target. A small aim-assist volume makes catching intentional and readable without turning it into long-range attraction.

## Design Delta

Currently, a moving box can only be picked up when the normal two-metre interaction scan happens to hit its collider precisely.

When the normal interaction scan finds no target, an empty-handed player may instead acquire a catchable box inside a short sphere cast centred on the aiming direction.

## New Rules / Values

- Catch distance from the character: `2.5 m`.
- Catch aim-assist radius: `0.4 m`.
- Minimum box speed: `1.5 m/s`.
- The player's hands must be empty.
- The box must have an active, non-kinematic Rigidbody and must not be stored on a cart.
- A box already held locally or remotely cannot be caught through aim assist.
- A `PackedOrderBox` taller than `1.35 m` cannot be caught.
- The closest eligible sphere-cast result is selected.
- Eligible boxes display `Catch box [bound interaction key]`.
- Catching calls the existing box interaction path. Local grabs stop the Rigidbody as usual; networked grabs use the existing server RPC, so the server resolves simultaneous attempts.

## Affected Systems

| System | Impact | Action Required |
|--------|--------|-----------------|
| Player interaction scan | Adds a catch-only fallback sphere cast | Update `PlayerInteraction` |
| Box interaction | Exposes catch eligibility and prompt | Update `BoxPhysicsInteractable` |
| Multiplayer box ownership | Reused unchanged | No new RPC |

## Acceptance Criteria

- [ ] An empty-handed player can catch an eligible moving box within `2.5 m` while aiming within the assist radius.
- [ ] The candidate displays `Catch box` with the current interaction binding.
- [ ] A stationary box continues to use the normal pickup path and prompt.
- [ ] A held, cart-stored, kinematic, slow, or oversized packed-order box is not selected by catch assist.
- [ ] Catching immediately transitions the box to the existing carry state.
- [ ] In multiplayer, simultaneous catches are resolved by the existing server-authoritative grab request.
- [ ] No regression: normal interaction priority and ordinary box pickup remain unchanged.

## GDD Update Required?

No. No project GDD or systems index is currently present; this isolated interaction addition is fully described here.
