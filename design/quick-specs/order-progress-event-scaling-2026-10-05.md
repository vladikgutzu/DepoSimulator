# Quick Design Spec: Order Progress Event Scaling

**Type**: Tweak
**System**: Random Events
**GDD Reference**: No GDD found
**Date**: 2026-10-05

## Change Summary

Increase random-event pressure as the team completes more orders. Order progress controls the
interval between event attempts, while the existing shift-time concurrency limits continue to
control how many incidents may overlap.

## Motivation

A 55-minute shift should build toward a busier finale. The current scheduler is driven only by
elapsed shift time, so an efficient team does not receive proportionally more disruption as it
moves through the 17-order catalog.

## Design Delta

The current scheduler chooses its next interval from three elapsed-time bands.

This spec changes interval selection to the following completed-order bands:

| Completed orders | Next-event interval | Existing concurrency limit |
|---:|---:|---:|
| 0-2 | 70-100 seconds | Time-driven, maximum 1 initially |
| 3-5 | 55-80 seconds | Time-driven, up to 2 |
| 6-9 | 40-65 seconds | Time-driven, up to 2 |
| 10-13 | 30-50 seconds | Time-driven, up to 3 |
| 14+ | 20-40 seconds | Time-driven, up to 4 |

## New Rules / Values

1. `GameManager.CompletedOrders` selects the interval band in both solo and multiplayer.
2. Multiplayer scheduling remains server-authoritative.
3. Crossing into a new order-progress band schedules a fresh full interval from that band, so an
   event never fires immediately merely because an order was completed.
4. Completing an order inside the same band does not disturb the already scheduled event.
5. Tutorial gates, active-event exclusion, role filters, and the mutual exclusion between
   `RushOrder` and `UrgentPart` remain unchanged.
6. Existing elapsed-time concurrency scaling remains unchanged.

## Affected Systems

| System | Impact | Action Required |
|---|---|---|
| Random-event scheduler | Interval pressure follows completed orders | Update `GameEventManager` |
| Game manager | Supplies completed-order count and completion notification | No contract change |
| Multiplayer | Server uses shared authoritative progress | No RPC change |

## Acceptance Criteria

- [ ] Each completed-order band returns an interval inside its documented range.
- [ ] Entering a higher band reschedules a complete interval rather than firing immediately.
- [ ] Completing an order without crossing a band preserves the pending schedule.
- [ ] No event is scheduled before the first order is accepted.
- [ ] Tutorial blocking and first-event teaching panels continue to work.
- [ ] Multiplayer clients do not schedule events locally.
- [ ] `RushOrder` and `UrgentPart` remain mutually exclusive.
- [ ] Existing maximum-concurrency progression remains unchanged.

## GDD Update Required?

No. No random-events GDD or systems index is present; this quick spec is sufficient for the
isolated scheduler tweak.
