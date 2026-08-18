# Choke-abo UI/UX Recommendations

**Review date:** 2026-08-18  
**Scope:** UI code review only; no runtime behaviour or implementation changes are included in this document.

## Product goal

Turn live racing-chocobo stats into a valid feed plan, exact purchase list, and understandable buy/feed automation run.

## Reviewed surfaces

- `ChokeAbo/Windows/MainWindow.cs`
- `ChokeAbo/Windows/ConfigWindow.cs`

## What is already working

- The main window places current/projected stats beside the training plan.
- The purchase table separates planned quantity, stock, currency, and total cost.
- Refresh and status-to-chat actions support troubleshooting without leaving the plugin.

## Prioritized recommendations

| Priority | Recommendation | Rationale and completion signal |
| --- | --- | --- |
| P0 | Make the two-column workspace responsive. | Stack Overview above Plan when width is constrained so labels, stat values, and G1/G2/G3 selectors never clip. |
| P0 | Spell out grade choices and selection state. | Use `Grade 1`, `Grade 2`, and `Grade 3` in an accessible segmented control with a visible selected state, not only narrow G1/G2/G3 radio cells. |
| P0 | Put automation readiness beside its action. | Show NPC proximity, data freshness, available sessions, currency, inventory, and selected plan as a checklist; every blocked Start needs a specific reason. |
| P1 | Show progress toward the intended result. | For each stat, show current, projected, cap, gain, and sessions consumed; summarize total sessions before the detailed table. |
| P1 | Group the shopping list by source and shortage. | Separate gil and MGP purchases, emphasize missing amounts, and expose the exact total needed before automation starts. |
| P1 | Make Clear Plan recoverable. | Confirm the action or offer a short Undo so a carefully entered plan cannot disappear on one click. |
| P2 | Remove implementation rollout notes from normal settings. | Keep user controls in Config; move rollout phases and concept recap to README or a developer-only About section. |

## Suggested information hierarchy

1. Data freshness and readiness
2. Current/projected stats
3. Training plan
4. Shopping list
5. Buy/feed progress

## Validation checklist

- A new user can identify the primary action and current blocker within five seconds.
- Every disabled control has a nearby plain-language reason and, when possible, a direct corrective action.
- Healthy, warning, error, running, and disabled states remain distinguishable without colour.
- The UI remains usable at narrow window widths and common Dalamud UI scales without clipped labels or unreachable controls.
- Destructive, global, or high-impact actions identify their scope and require confirmation or provide a safe undo.
- Empty, loading, stale-data, success, partial-success, and failure states each provide an appropriate next action.
- Settings clearly identify whether they apply globally, per account, per character, per preset, or only for the current session.
- Advanced diagnostics are still reachable but do not compete with the everyday workflow.

## Recommended implementation order

1. Implement P0 items and validate the primary workflow plus blocker recovery.
2. Implement P1 information-architecture and configuration improvements.
3. Apply P2 polish, then test at multiple UI scales with both fresh and mature configurations.
