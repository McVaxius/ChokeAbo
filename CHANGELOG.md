# Changelog

## 2026-08-28 - I237 race chocobo breeding

- Added per-character target-pedigree planning with deterministic enumeration of every fledgling, retired form, covering permission, and proof in item range `9560-9614`. Plans retain exact container/slot evidence, deterministic capacity and slot tie-breaking, target feeding, useful-candidate racing, missing-sex recovery, and 24-hour covering eligibility.
- Added persisted target-cycle ownership, requested inputs, phases, exact selection/action evidence, pause requests, transition times, and one-time migration of legacy covering waits. Purchases and one-session adaptive feed rounds persist intent before acting and reconcile fresh inventory/stat evidence before advancing.
- Retained the fail-open `ChokeAbo.Breeding.ShouldBlockRacing.V1` bool endpoint and added strict JSON V2 Ensure, Status, and Pause endpoints for VERMAXION target mode. Manual breeding no longer auto-starts, and V2 pause requests stop only target-owned work at a safe boundary.
- Added `/chokeabo dpopup`, a passive one-session-at-a-time recorder for retirement, covering selector, fledgling selector, and adoption evidence. Capture sessions append addon lifecycle/setup/node/event/list-owner, racer, and exact inventory delta data to Markdown files in the plugin configuration folder.
- Removed guessed first-row and generic dialog selections. Retirement, covering, fledgling registration, and adoption stop at named capture boundaries until the operator supplies `retirement.md`, `cselector.md`, `fselector.md`, and `adoption.md`; live target-cycle acceptance remains pending.
- The Debug x64 project builds successfully without a version bump or new dependency. Runtime dialog mapping and live-game verification were not performed.

## 2026-07-01

- Restored the delayed GoldSaucerInfo Chocobo tab callback sequence with payload `130` and payload `131` fallback: `/callback GoldSaucerInfo true 0 1 130`, `/callback GoldSaucerInfo true 19 0 130`, `/callback GoldSaucerInfo true 0 1 131`, and `/callback GoldSaucerInfo true 19 0 131`.

## 2026-03-25

- Bootstrapped the `Choke-abo` repository shell.
- Added the Dalamud project, solution, plugin manifest, windows, and DTR/Ko-fi baseline.
- Added icon assets at `images\iconHQ.png` and `images\icon.png`.
- Added the initial import guide and README.
