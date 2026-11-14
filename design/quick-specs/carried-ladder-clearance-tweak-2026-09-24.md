# Quick Design Spec: Carried Ladder Clearance

**Type**: Tweak
**System**: Portable ladder carrying
**GDD Reference**: No GDD found
**Date**: 2026-09-24

## Change Summary

Keep the ladder's physical collider disabled during carrying and use its
authored BoxCollider dimensions for a movement sweep before the player's
CharacterController moves.

## Motivation

Disabling all ladder collision lets its large visual volume pass through
shelving. Enabling the kinematic collider only activates camera avoidance; it
does not cause CharacterController to undo its movement when the carried child
touches static geometry.

## Design Delta

The carried ladder previously relied on either no collision or a solid child
collider. Neither approach constrained the actor that owns the movement.

This specification makes player movement explicitly account for the carried
ladder volume before CharacterController.Move is called.

## New Rules / Values

- The solid ladder collider stays disabled in the Carried state.
- Before horizontal movement, the ladder performs a BoxCast matching its
  authored collider position, rotation, and scaled size.
- Static walls, shelves, and structural geometry limit the requested movement.
- The cast ignores triggers, the carrier, the ladder hierarchy, floor-facing
  contacts, and objects with their own Rigidbody.
- Vertical player motion remains unchanged.
- The owning player performs the check; observers receive the resulting valid
  position through existing network synchronization.
- The clearance skin defaults to 0.04 metres.

## Affected Systems

| System | Impact | Action Required |
|--------|--------|-----------------|
| Ladder carrying | Supplies a constrained motion vector | Add a BoxCast-based clearance API |
| Third-person movement | Applies the carry constraint before moving | Route requested motion through LadderController |
| Follow camera | No longer sees a solid carried ladder | Restore the previous camera distance |
| Multiplayer | Owner remains authoritative for movement | No new network message required |

## Acceptance Criteria

- [ ] The camera remains at its normal distance while carrying a ladder.
- [ ] The ladder cannot be walked through shelves or walls.
- [ ] The player can back away after the ladder reaches an obstacle.
- [ ] Floor contact does not prevent horizontal movement.
- [ ] Dynamic boxes and pallets do not make the carrier stick.
- [ ] Jumping, falling, placing, leaning, and climbing remain unchanged.
- [ ] Remote players receive the owner's collision-safe position normally.

## GDD Update Required?

No. No project GDD or systems index is present; this isolated movement tweak is
fully described by the quick spec.
