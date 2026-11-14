# Quick Design Spec: Battery Guidance

**Type**: Addition
**System**: Battery, Tutorial, Minimap
**GDD Reference**: No relevant GDD or systems index found.
**Date**: 2026-10-05

## Change Summary

Charging stations become permanent points of interest on the Warehouse Map. The first time the local robot's charge reaches 80% or less, the game presents a one-time charging explanation.

## Motivation

Battery drain and charging currently begin without onboarding. Players can see their charge falling but are not told where stations are, how charging modes differ, or how to disconnect.

## New Rules / Values

- Every active `ChargingStation` is shown on the minimap using its actual world transform.
- Charging markers use a green-cyan lightning icon, the label `Charge`, and a matching legend entry.
- Each local player receives the battery popup once per gameplay session when charge first becomes `<= 80%`.
- If another tutorial/event card is active, the battery popup waits until that card is closed.
- The popup never changes `Time.timeScale`, cursor state, or player input; it only explains that the robot uses a battery and directs the player to the nearest marked station.
- Slow/Fast Charge explanations, rates, prices and the disconnect hint are shown inside the station's mode-selection panel.
- Automatic docking moves at walking speed and drives the walk clip rather than the run blend.
- Once stopped on `ChargePoint`, the humanoid smoothly blends into an A-pose over 0.45 seconds.
- The charging pose stands 0.05 Unity units above the robot's pre-dock floor height so it reads as standing on the station platform without visibly floating.
- Manual or automatic disconnect immediately blends out of A-pose while the robot walks forward and smoothly descends to its saved pre-dock height.
- Charging blocks melee input and hit resolution. Multiplayer validates the same restriction on the server.
- Multiplayer copies derive the same visual pose from the authoritative charging state.
- Recharging above 80% does not reset the one-time trigger.

## Affected Systems

| System | Impact | Action Required |
|---|---|---|
| Battery | Detect first local threshold crossing | Runtime code update |
| Tutorial | Queue and present an independent informational card | Runtime code update |
| Minimap | Add station POIs and legend entry | Runtime code update |
| Input/Pause | Pause solo or block local multiplayer input | Reuse existing UI/GamePause paths |

## Acceptance Criteria

- [ ] Every charging station appears at the correct minimap position.
- [ ] The popup appears once at 80% or below in solo and multiplayer.
- [ ] It does not replace or corrupt the active base tutorial/event card.
- [ ] The popup animates fully while the world and local player controls continue running.
- [ ] Charging above and below 80% again does not repeat the popup.
- [ ] Existing charging interaction and minimap event markers continue to work.
- [ ] Dock approach visibly walks, and entry/exit from A-pose has no hard snap.

## GDD Update Required?

No. This addition is below systems-index tracking threshold; the quick spec is sufficient.

