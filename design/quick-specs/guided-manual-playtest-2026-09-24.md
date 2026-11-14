# Quick Design Spec: Guided Manual Playtest

**Type**: New Small System
**Scope**: An editor/development-only guided playtest that presents the game's critical manual scenarios in a fixed order, isolates them from random incidents, and records tester verdicts. It does not alter release gameplay.
**Date**: 2026-09-24
**Estimated Implementation**: 6-10 hours

## Overview

`Guided QA Mode` starts the training warehouse from `Tools/QA/Start Guided Playtest` and displays a persistent test card over the Game view. Each card tells the tester what to do and what should be visible. The tester explicitly starts and grades each case; objective setup such as starting an event is automated, while subjective animation, camera, collision, and presentation quality remains a human verdict. Results, console errors, comments, and failure screenshots are written to a Markdown report.

## Core Rules

1. Guided QA is available only in the Unity Editor or a Development Build.
2. Starting it opens `Assets/urp_scene_with_training 1.unity`, enters Play Mode, and preserves the current test index across scene reloads.
3. The random event scheduler is disabled for the duration of the run. Named event cases still use `GameEventManager.ForceStartEvent`, so their normal tutorial, timer, marker, spawning, and cleanup paths remain under test.
4. Only one case is active. The next case never starts on a timer and requires `PASS`, `FAIL`, or `SKIP`.
5. The overlay exposes `START TEST`, `PASS`, `FAIL`, `RETRY`, `SKIP`, and `STOP`. A free-text note is stored with the verdict.
6. Event cases are cleaned up before advancing. `RETRY` reloads the warehouse and resumes the same case.
7. `FAIL` captures a Game-view screenshot and stores console errors observed during that case.
8. The ordered suite covers tutorial, three regular orders, carry/IK/highlight presentation, cart, forklift, ladder, Rush Order, Urgent Part, every registered random event, zombie combat/death, and the end-of-shift Steam call-to-action.
9. Automatic observations supplement but never replace a human visual verdict. The overlay reports whether the requested event was accepted and remains pending/active/completed.
10. Stopping or finishing clears the QA flag. Normal sessions retain the ordinary random scheduler and UI.

## Tuning Knobs

| Knob | Default | Range | Category | Rationale |
|------|---------|-------|----------|-----------|
| Case count | 30 | 1-100 | gate | Covers the current manual regression surface without changing gameplay |
| Setup delay | 0.5 s | 0-5 s | feel | Allows scene singletons to finish initialization before setup |
| Panel width | 520 px | 380-900 px | feel | Readable alongside a 16:9 Game view |

Case definitions are kept in the QA controller rather than production balance data because they are editor-only executable test documentation, not gameplay tuning.

## Ordered Cases

1. Solo launch and base tutorial.
2. First, second, and third regular orders.
3. Box-size carry presentation, packed-order IK/lean/walk, and complete-box highlight.
4. Cart placement/movement/exit, forklift camera/movement/forks/exit, and ladder carry/collision/placement.
5. First and repeat Rush Order; first and repeat Urgent Part.
6. Broken Forklift, Broken Fork, Power Failure, Wet Floor, Shelf Collapse, Bench Malfunction, Wrong Order, Supply Delay, Radio Interference, Thief, Fire Alarm, and Gate Breach.
7. Maximum hit and thrown-box zombie death response.
8. End-of-shift popup and Steam wishlist action.

## Acceptance Criteria

- [ ] `Tools/QA/Start Guided Playtest` opens the training warehouse and enters Play Mode.
- [ ] The overlay presents all 30 cases in a stable order.
- [ ] No case advances without a tester verdict.
- [ ] Random events cannot interrupt the active case.
- [ ] Named event cases use the real event-manager path and show start status.
- [ ] Event cleanup releases its slot before the next case.
- [ ] `RETRY` reloads the scene and resumes the same case.
- [ ] `FAIL` produces a screenshot and includes case-local console errors in the report.
- [ ] Finishing produces a Markdown report under `production/qa/`.
- [ ] Stopping or leaving Play Mode clears the active QA run.
- [ ] Release builds contain no active Guided QA behavior.
- [ ] Existing automated smoke coverage remains green.

## Systems Index

No systems index exists. This editor/development-only tool is below the gameplay systems-index tracking threshold; this quick spec is sufficient.
