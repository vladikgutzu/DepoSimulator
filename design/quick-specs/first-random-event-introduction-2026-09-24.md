# Quick Design Spec: First Appearance Event Introductions

**Type**: Addition
**System**: Tutorial / Random Events
**GDD Reference**: No GDD found
**Date**: 2026-09-24

## Change Summary

In solo play, display a short objective card the first time each non-obvious
event type appears. Keep that event pending until the player presses Continue;
repeat appearances start normally with only their notification and objective.

## Motivation

Several incidents require actions the player cannot infer from their title alone.
The player should learn each mechanic once without losing time to a timer, while
repeat events remain fast and unobtrusive.

## Design Delta

The event scheduler normally applies a selected event immediately.

This specification changes the first solo appearance of each taught type to a
two-stage start: select and hold, show its objective, then apply after Continue.

## New Rules / Values

1. Cards appear only after the base tutorial presentation finishes.
2. Taught types are `BrokenForklift`, `BrokenFork`, `PowerFailure`, `UrgentPart`,
   `RushOrder`, `Thief`, `FireAlarm`, and `GateBreach`.
3. Each card states one concrete goal in plain language.
4. The selected event performs no world changes and starts no timer before Continue.
5. UI Continue, Enter, and the gamepad south button confirm the card.
6. Applying the event after Continue reveals its existing marker, colour and
   short notification objective.
7. Every taught type shows its card at most once per session.
8. Obvious events such as `WetFloor` start normally without a card.
9. Co-op scheduling is unchanged; skipping training suppresses the cards.
10. Restarting the session resets first-appearance state.

## Affected Systems

| System | Impact | Action Required |
|--------|--------|-----------------|
| Random-event scheduler | Holds the first solo occurrence of each taught type | Track introduced types and release one pending event |
| Tutorial manager | Presents type-specific objectives and handles Continue | Map event names to concise teaching content |
| HUD tutorial banner | Reused without visual changes | No layout change |
| Toolkit repair loop | Broken vehicle and power-outage lessons require the item they teach | Enable toolkit spawning and requirement; Fire Alarm remains exempt |

## Acceptance Criteria

- [ ] Taught events wait until the base tutorial presentation is complete.
- [ ] A type-specific card appears before the first occurrence changes the world or starts a timer.
- [ ] Continue works from UI, Enter, and the gamepad south button.
- [ ] Pressing Continue closes the card and starts the exact event already selected.
- [ ] The card does not appear for later occurrences of the same type.
- [ ] Each other taught type still receives its own first-use card.
- [ ] Wet Floor and other obvious events start without a blocking card.
- [ ] Broken vehicle and Power Failure events spawn and require the marked toolkit.
- [ ] Fire Alarm power boxes remain repairable without a toolkit.
- [ ] Skipping training suppresses all event teaching cards.
- [ ] Co-op event behaviour remains unchanged.
- [ ] Restarting the session allows the card to appear again.

## GDD Update Required?

No. No project GDD or systems index is present; this quick spec is sufficient
for the isolated onboarding addition.
