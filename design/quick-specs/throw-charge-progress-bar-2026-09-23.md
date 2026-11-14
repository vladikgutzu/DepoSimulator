# Quick Design Spec: Throw Charge Progress Bar

**Type**: Tweak
**System**: HUD / throw charge feedback
**GDD Reference**: No GDD found
**Date**: 2026-09-23

## Change Summary

Restyle the existing throw-charge indicator to use the same labelled progress-bar language as the rest of the HUD. Throw timing, force, thresholds, and placement remain unchanged.

## Motivation

The current thick, glossy, unlabelled meter looks like a separate visual system and does not explain what is charging. A labelled standard HUD bar makes the feedback consistent and immediately understandable.

## Design Delta

The current indicator is a 14-pixel glossy capsule with a light border and no label.

It becomes a compact `THROW POWER` block containing the shared five-pixel progress track and flat progress fill used elsewhere in the HUD. Every visible HUD progress bar must have a nearby text label that names the value it represents; the existing order deadline bar therefore receives an `ORDER TIME` label as well.

## New Rules / Values

- The indicator remains centred beneath the aiming point.
- The container remains 240 pixels wide.
- A `THROW POWER` label appears directly above the track.
- The track uses the shared `progress-track` class.
- The fill uses the shared `progress-fill` class.
- The decorative gloss child and white outer border are removed.
- The fill remains amber at low charge, orange at strong charge, and red at full charge.
- Fill width updates immediately with no easing so it accurately tracks held input.
- The order deadline bar displays `ORDER TIME`; the fuel bar keeps its existing `FUEL` label.

## Affected Systems

| System | Impact | Action Required |
|--------|--------|-----------------|
| HUD layout | Adds label and shared track wrapper | Update `HUD.uxml` |
| HUD styling | Reuses shared progress styles | Update `HUD.uss` |
| Throw mechanic | None | No code changes |

## Acceptance Criteria

- [ ] Throw charge displays the label `THROW POWER`.
- [ ] Its track and fill match the dimensions and shape of other HUD progress bars.
- [ ] The meter stays centred beneath the aiming point.
- [ ] Amber, orange, and red charge states remain visible.
- [ ] The order deadline and fuel progress bars retain visible identifying labels.
- [ ] No regression: charge timing, throw force, and input response are unchanged.

## GDD Update Required?

No. No project GDD or systems index is currently present; this isolated HUD visual tweak is fully described here.
