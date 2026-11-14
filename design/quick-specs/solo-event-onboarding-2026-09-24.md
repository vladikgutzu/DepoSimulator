# Quick Design Spec: Solo Event Onboarding

**Status**: Superseded by `first-random-event-introduction-2026-09-24.md`

The fixed Rush Order / normal order / Urgent Part sequence described below is
retained for history but is not the active design. The approved rule is now:
each non-obvious event teaches its concrete objective on its first natural
appearance, while repeat appearances use only normal markers and notifications.

**Type**: Addition
**System**: Tutorial / Random Events
**GDD Reference**: No GDD found
**Date**: 2026-09-24

## Change Summary

Add controlled, step-by-step introductions to `Rush Order` and `Urgent Part` in
solo play. The lessons are placed between the three normal orders so the player
first learns the core warehouse loop, then learns each exceptional delivery
type without simultaneous random incidents.

## Motivation

The two event types currently appear as ordinary random incidents. Their box
colours and destination colours communicate a relationship, but a new player
has not been taught that relationship, that the tasks are timed, or that an
urgent part cannot be handed to the assembly bench. A short guided introduction
makes the colour language and delivery rules explicit before free play begins.

## Design Delta

The current tutorial ends after the first normal order and immediately moves
the player into free practice while `GameEventManager` can schedule its complete
random pool.

This specification changes the solo-session sequence to:

1. Complete the first normal order through the existing base tutorial.
2. Complete a guided `Rush Order` lesson.
3. Complete the second normal order independently, without tutorial prompts.
4. Complete a guided `Urgent Part` lesson.
5. Unlock the third normal order and the complete random-event pool.

## New Rules / Values

### Rush Order lesson

1. After `TRAINING COMPLETE`, display an informational `RUSH ORDER` card.
2. Explain that target boxes and their Shipping Zone are purple and that the
   task is timed.
3. Do not create the event or begin its timer until the player presses
   `Continue`.
4. Once accepted, start the Rush Order and show these action-driven steps:
   - `FIND A PURPLE BOX`
   - `PICK UP THE RUSH BOX`
   - `DELIVER THE BOX`
5. The delivery instruction points the player to the purple Shipping Zone.
6. After the first delivery, explain the remaining-box counter. The existing
   event notifications continue to report later deliveries.
7. Event completion or timeout ends the lesson and directs the player to accept
   the second normal order.
8. Delivery to the assembly bench remains mechanically valid, but the guided
   path teaches the colour-matched Shipping Zone.

### Second normal order

- The player completes the second normal order without step-by-step prompts.
- Random events remain suspended so this order verifies that the player can
  repeat the core loop independently.

### Urgent Part lesson

1. After completion of the second normal order, display an informational
   `URGENT PART` card.
2. Explain that the target box and Shipping Zone are red, the task is timed,
   and the assembly bench does not accept this delivery.
3. Do not create the event or begin its timer until the player presses
   `Continue`.
4. Once accepted, start the event and show these action-driven steps:
   - `FIND THE RED BOX`
   - `PICK UP THE URGENT PART`
   - `DELIVER IT TO THE RED SHIPPING ZONE`
5. Delivery or timeout ends the lesson and unlocks free play.

### Event scheduling and modes

- The onboarding sequence runs only in solo play.
- The existing co-op tutorial skip remains unchanged.
- Automatic random-event scheduling stays suspended through the base tutorial,
  Rush Order lesson, second normal order, and Urgent Part lesson.
- Only one guided event may be active at a time.
- After the Urgent Part lesson ends, the full random-event pool is enabled.
- Skipping the base tutorial marks event onboarding complete and enables the
  ordinary random-event scheduler; experienced players are not forced through
  the two event lessons.
- Restarting the session resets the onboarding sequence.
- The existing tutorial panel, active input-binding icons, event mechanics,
  event timers, highlight colours, and Shipping Zone colours are reused.

## Affected Systems

| System | Impact | Action Required |
|--------|--------|-----------------|
| Tutorial state machine | Adds two post-tutorial lesson sequences | Extend or add a focused solo event-onboarding controller |
| Random-event scheduler | Must remain paused until onboarding finishes | Add an explicit onboarding scheduling gate |
| Rush Order | Needs guided start and observable pickup/delivery progress | Expose or reuse existing event state without changing normal event rules |
| Urgent Part | Needs guided start and observable pickup/delivery progress | Expose or reuse existing event state without changing normal event rules |
| HUD tutorial panel | Displays post-tutorial informational and action steps | Reuse existing tutorial presentation and binding resolution |

## Acceptance Criteria

- [ ] Completing the first order finishes the existing base tutorial and opens the Rush Order introduction.
- [ ] Rush Order does not exist and its timer does not run before `Continue`.
- [ ] Rush Order steps advance on finding, picking up, and delivering a purple target box.
- [ ] The second normal order has no tutorial prompts or random incidents.
- [ ] Completing the second normal order opens the Urgent Part introduction.
- [ ] Urgent Part does not exist and its timer does not run before `Continue`.
- [ ] Urgent Part steps advance on finding, picking up, and delivering the red target box.
- [ ] The Urgent Part lesson explicitly states that the assembly bench does not accept the box.
- [ ] Box and Shipping Zone colours match throughout both lessons.
- [ ] Ending the Urgent Part lesson unlocks the complete random-event pool.
- [ ] Skipping the base tutorial immediately enables ordinary random-event scheduling.
- [ ] Co-op flow and event behaviour are unchanged.
- [ ] Restarting the session begins the solo onboarding sequence from its initial state.

## GDD Update Required?

No. No project GDD or systems index is currently present; this quick spec is
sufficient for the isolated onboarding addition.
