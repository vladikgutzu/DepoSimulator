# Quick Design Spec: Vehicle Camera Sensitivity

**Type**: Tuning
**System**: Vehicle camera controls
**GDD Reference**: No vehicle-controls GDD found
**Date**: 2026-09-24

## Change

| Parameter | Old Value | New Value | Rationale |
|-----------|-----------|-----------|-----------|
| Shop cart camera sensitivity | 0.35 | 0.20 | Make camera movement easier to control while pushing the cart. |
| Shop forklift camera sensitivity | 0.35 | 0.20 | Make camera movement easier to control while driving the forklift. |

## Tuning Knob Mapping

No documented GDD tuning range exists. The vehicle-specific value remains a
multiplier of the player's global look-sensitivity setting.

## Acceptance Criteria

- [ ] The cart camera uses a vehicle sensitivity multiplier of 0.20.
- [ ] The forklift camera uses a vehicle sensitivity multiplier of 0.20.
- [ ] Both vehicle cameras rotate approximately 43% slower than before at the
      same global look-sensitivity setting.
- [ ] The regular on-foot camera sensitivity is unchanged.
