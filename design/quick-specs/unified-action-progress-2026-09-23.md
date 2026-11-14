# Quick Design Spec: Unified Action Progress

**Type**: Tweak
**System**: Gameplay HUD / held actions
**GDD Reference**: No GDD found
**Date**: 2026-09-23

## Change Summary

Replace the different gameplay hold indicators used by melee charging, box throwing, and repairs with one labelled Action Progress HUD component. Action timing and outcomes do not change.

## Motivation

Melee and repair draw progress with block characters inside the interaction prompt, while throwing uses a real UI Toolkit bar elsewhere. The inconsistent shape, placement, and missing melee label make equivalent hold interactions look unrelated.

## Design Delta

All continuous player-controlled actions use the same centred progress component with an uppercase action label and the shared HUD progress track/fill. Text-character progress bars are removed.

## New Rules / Values

- Labels identify attack, throw, each repair target, self-repair, and limb fitting.
- Unknown holds use `ACTION PROGRESS`.
- The bar is amber at low progress, orange from 66%, and red at full progress.
- The ordinary prompt remains before a repair hold, is replaced during the hold, and returns if cancelled.
- Progress width follows input immediately.

## Acceptance Criteria

- [ ] Attack, throw, and every repair/fitting hold use the same labelled component.
- [ ] No gameplay hold displays a block-character progress bar.
- [ ] Completing or cancelling hides and resets the component.
- [ ] Hold durations, attack thresholds, throw force, and repair behavior do not change.

## GDD Update Required?

No. No project GDD or systems index is currently present.
