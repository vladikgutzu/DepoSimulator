# Quick Design Spec: Ladder Carry Distance

**Type**: Tuning
**System**: Portable ladder carrying
**GDD Reference**: No GDD found
**Date**: 2026-09-24

## Change

| Parameter | Old Value | New Value | Rationale |
|-----------|-----------|-----------|-----------|
| Ladder carry local Z offset | 0.35 | 0.80 | Leave more space between the player and the carried ladder. |

## Tuning Knob Mapping

No documented GDD range exists. The offset is authored in the carrier's local
space and the carried-ladder BoxCast automatically follows the new position.

## Acceptance Criteria

- [ ] The ladder is carried at local Z 0.80.
- [ ] The prefab keeps its existing local X and Y carry offsets.
- [ ] The carried-ladder clearance sweep follows the new position.
- [ ] Hand IK remains visually acceptable while walking and running.
- [ ] The player can place the ladder normally.
