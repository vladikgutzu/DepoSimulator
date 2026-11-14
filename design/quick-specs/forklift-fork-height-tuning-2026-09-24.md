# Quick Design Spec: Forklift Fork Height

**Type**: Tuning
**System**: Forklift fork controls
**GDD Reference**: No forklift-controls GDD found
**Date**: 2026-09-24

## Change

| Parameter | Old Value | New Value | Rationale |
|-----------|-----------|-----------|-----------|
| Visual fork base world offset | 0 m | +0.5 m | Keep the fork geometry above the warehouse floor. |
| Minimum fork world height | 0.05 m | 0.55 m | Move the lower control limit with the visual fork. |
| Maximum fork world height | 3.8 m | 4.3 m | Preserve the original lift travel after raising its base. |

## Tuning Knob Mapping

No documented GDD tuning range exists. The visual correction is applied in
world space so it remains independent of the imported FBX bone scale.

## Acceptance Criteria

- [ ] The forklift fork is 0.5 m higher at its minimum position.
- [ ] The original fork travel distance is preserved.
- [ ] The pallet pickup mount follows the corrected visual fork position.
- [ ] Network fork synchronization continues to use the unchanged normalized
      lift range.
