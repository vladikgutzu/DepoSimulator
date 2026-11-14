# Quick Design Spec: Solo-Only Free Practice Notice

**Type**: Tweak
**System**: Tutorial / Multiplayer HUD
**GDD Reference**: No GDD found
**Date**: 2026-10-05

## Change Summary

Keep the post-training `Free practice` notification in single-player and suppress it in
multiplayer, where it does not unlock or change any gameplay state.

## Motivation

In multiplayer the notice looks like a transition into a separate mode even though order
availability, roles, events, and progression are unaffected. It is useful guidance for a solo
player leaving the guided first order, but redundant and confusing for a coordinated team.

## Design Delta

The first completed order currently shows `Free practice` in every game mode.

After this change, the tutorial still completes and releases its presentation gate in every mode,
but only a non-networked session displays the delayed `Free practice` notification.

## New Rules / Values

1. `TRAINING COMPLETE` remains unchanged.
2. `_baseTutorialPresentationComplete` is set in both solo and multiplayer.
3. `Free practice` is displayed only when no `NetworkManager` session is listening.
4. Multiplayer receives no replacement notification.
5. Order unlocking, event scheduling, tutorial subscriptions, and networking remain unchanged.

## Affected Systems

| System | Impact | Action Required |
|---|---|---|
| Tutorial manager | Mode-gates one delayed notification | Add a guard around `AddNotification` only |
| Multiplayer HUD | Removes a misleading message | No layout or RPC changes |

## Acceptance Criteria

- [ ] Solo shows `Free practice` after completion of the guided first order.
- [ ] Multiplayer does not show `Free practice` after its first completed order.
- [ ] Tutorial completion state is released in both modes.
- [ ] Subsequent orders remain available in both modes.
- [ ] Random-event scheduling and first-use event introductions are unchanged.

## GDD Update Required?

No. This is an isolated presentation correction and no tutorial GDD or systems index is present.
