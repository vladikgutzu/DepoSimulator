# Quick Design Spec: Ladder Carry Height

**Type**: Tuning
**System**: Portable ladder carrying
**GDD Reference**: No GDD found
**Date**: 2026-09-24

## Change

| Parameter | Old Value | New Value | Rationale |
|-----------|-----------|-----------|-----------|
| Ladder carry local Y offset | 0.00 | 0.20 | Raise the carried ladder slightly so its clearance sweep does not catch on the floor and slow the player. |

## Tuning Knob Mapping

No documented GDD range exists. The offset is authored in the carrier's local
space and the carried-ladder BoxCast automatically follows the new position.

## Acceptance Criteria

- [ ] The ladder is carried at local Y 0.20.
- [ ] The prefab keeps its existing local X and Z carry offsets.
- [ ] The carried-ladder clearance sweep follows the new position.
- [ ] The player no longer slows down on ordinary flat-floor movement.
- [ ] The ladder still blocks movement through shelves and walls.
- [ ] Hand IK and ladder placement remain visually acceptable.
