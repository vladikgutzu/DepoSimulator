# Quick Design Spec: Box Drop, Bench Delivery, Purchase, and UI Audio

**Type:** Addition  
**System:** Audio / Box Physics / Assembly Bench / Shop / UI  
**Date:** 2026-10-03  
**GDD reference:** No dedicated audio GDD exists. This change extends `design/audio/audio-warehouse-gameplay.md`.

## Change Summary

Replace the long box-handling recording in automatic gameplay events with short,
event-specific feedback. Add confirmed feedback for bench delivery and shop purchases,
and add consistent button-click feedback to the main UI Toolkit surfaces.

## Design Rules

### Box handling and impacts

- `box_handling_impacts_32135.mp3` must not be assigned to `Pickup`, `ReturnPart`,
  `BoxDrop`, or `BoxImpact`.
- The source file remains in the project and provenance manifest, but does not play
  automatically until clip-region or transient extraction support exists.
- A box landing after a meaningful fall plays the short
  `box_drop_light_453026.mp3` cue.
- Minor contact, resting jitter, and settled stacks remain silent.
- Repeated impacts from the same box use an approximately 140 ms per-box cooldown.
- Pickup and return actions use short cues and must never start a long recording that
  continues after the box is released or destroyed.

### Assembly bench delivery

- Successfully installing the contents of a carried box at the assembly bench plays one
  short installation-confirmation sound.
- The sound plays only after the local operation succeeds in single-player or after the
  server accepts it in multiplayer.
- Rejected, stale, out-of-range, or duplicate installation attempts do not play the
  confirmation sound.
- Host execution and the client replication echo must not produce two sounds for one
  installed part.
- The sound is associated with the assembly bench rather than with the destroyed box.

### Successful purchase

- Add a `PurchaseSuccess` audio event using a short shop/cash-register `cha-ching` clip
  sourced from an official Pixabay sound-effects page.
- Play it only after `OnPurchaseResolved` reports success.
- Do not play it on the initial button press, while the request is pending, or after a
  rejected purchase.
- Only the purchasing player's local UI plays this feedback.
- Single-player and multiplayer follow the same confirmation rule.

### Button feedback

- Add a quiet `UiButtonClick` audio event.
- Use root-level UI Toolkit event delegation so dynamically created buttons are covered
  without registering a callback on every button.
- Main menu, lobby, and gameplay HUD buttons are covered.
- An enabled button produces exactly one click sound per accepted click.
- Disabled buttons, programmatic value changes, and pointer movement remain silent.
- Shared UI roots must be bound only once, preventing duplicate menu/lobby clicks.
- A shop purchase click produces the normal button click immediately; a successful
  authoritative result produces the separate `PurchaseSuccess` cue later.

## Technical Constraints

- Do not add per-frame object searches or audio polling.
- Reuse the existing audio one-shot pool and data-driven catalog.
- Do not create an `AudioSource` per box or per UI button.
- Preserve server authority for bench delivery and purchase results.
- Keep settled box stacks silent and avoid collision-audio spam.
- Record any new Pixabay asset URL, author, download date, hash, and AI label status in
  `Assets/Audio/Pixabay/SOURCES.md`.

## Affected Systems and Files

- Audio event definitions and catalog setup.
- Box pickup, return, drop, and impact routing.
- Assembly bench installation confirmation.
- Shop purchase-result feedback.
- UI Toolkit click routing for main menu/lobby and HUD roots.
- Audio implementation specification and Pixabay provenance manifest.

## Acceptance Criteria

1. Dropping any physical box produces one short `box_drop_light` landing cue when the
   impact exceeds the configured threshold.
2. Settled boxes and low-energy contact do not repeatedly produce impact sounds.
3. `box_handling_impacts_32135.mp3` is not started by pickup, return, drop, collision, or
   bench delivery.
4. Carrying a box to the bench and walking away cannot leave a long handling sound
   playing.
5. A successfully installed part produces one short bench confirmation sound on every
   intended listener, with no host double-play.
6. A rejected bench transaction produces no installation-confirmation sound.
7. A confirmed successful purchase produces one local `cha-ching` in both single-player
   and multiplayer.
8. A rejected purchase produces no `cha-ching` and retains the existing error feedback.
9. Each enabled UI Toolkit button in the main menu, lobby, and HUD produces one quiet
   click, including dynamically created buttons.
10. Disabled buttons and programmatic UI updates remain silent.
11. The audio catalog contains an assigned cue for every `GameAudioEvent`, and Unity
    compiles with zero errors.

## Documentation Impact

No gameplay GDD update is required. Update the audio implementation specification so its
box, bench, shop, and UI event tables match the implemented behavior.
