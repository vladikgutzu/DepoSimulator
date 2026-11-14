# Quick Design Spec: Box Throw Tutorial

**Type**: Addition
**System**: Tutorial / box interaction
**GDD Reference**: No GDD found
**Date**: 2026-09-23

## Change Summary

Add a mandatory tutorial step after the player picks up the first required-part box. The step teaches the existing hold-and-release throw interaction through one successful throw before the order tutorial continues.

## Motivation

The throw mechanic is available during normal play, but the onboarding currently teaches only picking up and placing boxes. Requiring one throw makes the control discoverable and lets the player see the charge feedback in a safe early-game context.

## Design Delta

The existing tutorial advances directly from picking up the first required-part box to the general carrying instructions.

This spec changes that sequence to:

1. Pick up the first required-part box.
2. Hold the bound interaction control long enough to charge a throw.
3. Release the control to throw the box.
4. Continue to the existing carrying and assembly-bench instructions.

## New Rules / Values

- The lesson is inserted immediately after `PICK UP A PART` and before `ONE BOX AT A TIME`.
- Its title is `THROW A BOX`.
- Its copy explains charging, releasing, throw distance, and picking the box up again.
- The displayed key hint resolves through the current interaction binding.
- A short tap that merely puts the box down does not complete the lesson.
- Only the existing charged-throw path completes the lesson.
- The existing co-op tutorial skip remains unchanged.

## Affected Systems

| System | Impact | Action Required |
|--------|--------|-----------------|
| Tutorial state machine | One event-driven step added | Update `TutorialManager` |
| Box interaction | Emits notification after a valid throw | Update `PlayerInteraction` |
| Input rebinding | Tutorial must show active binding | Reuse existing `InputHints` resolution |

## Acceptance Criteria

- [ ] The throw lesson appears after the first required-part box is picked up.
- [ ] The lesson displays the player's current interaction binding.
- [ ] Tapping the interaction control to put the box down does not finish the lesson.
- [ ] Holding beyond the throw threshold and releasing finishes the lesson.
- [ ] The tutorial then proceeds to `ONE BOX AT A TIME` and the normal order flow.
- [ ] No regression: throw force, charge feedback, box physics, and co-op behavior remain unchanged.

## GDD Update Required?

No. No project GDD or systems index is currently present; this quick spec is sufficient for the isolated onboarding addition.
