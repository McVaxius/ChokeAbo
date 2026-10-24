# ChokeAbo LCARS concepts

Approved LCARS design concepts. Implementation is selected for the repositories in Z:\myrepos.txt, with the rollout tracked in Z:\!todo\2026-10-22GOATFUCK0002.md. These images are design references, not runtime or test evidence.

- [Main: regular and compact](01-main-regular-compact.png)
- [Settings, Mini and supporting surfaces](02-settings-mini-and-auxiliary.png)

The second sheet depicts selected existing supporting surfaces. Other source windows retain their established layout and actions under the proposed shared appearance; this is not a claim that every scroll position or conditional state has a separate image. Browser companions retain browser controls. Main/Mini windows are distinct from density variants. Generated text and examples are design material, not runtime evidence.

## Source scope

Source commit: `4724a8545c4b755737981726f7cf397c1604af8e`. Existing dirty paths were inspected by count (0); none are included or changed by this mockup task.

## Source-verified control contract

- `ChokeAbo/Windows/MainWindow.cs:105` draws Enabled and DTR Bar toggles, Settings, Refresh Chocobo Data, Status to chat, progression overview and manual workspace; Main has no tab navigation to invent.
- `MainWindow.cs:204` owns progression status and gated Resume/Pause/Stop, with Open VERMAXION settings for its actual goal configuration. Pedigree, Racing rank, Inherited ability, Learned ability, Goal, Colour, Phase, Covering ready in and Next action are read-only fields.
- `MainWindow.cs:605` defines Training Plan with Stat / Qty / Grade 1 / Grade 2 / Grade 3; `:700` defines Feed / Plan / Qty on hand / Currency / Total. `:753` defines explicit manual-cycle actions. Breeding stock cleanup uses preview selection and permanent-discard gate (`:410`), not immediate cleanup.
- `ChokeAbo/Windows/ConfigWindow.cs:35` has Settings and Window appearance tabs. Its General controls are Plugin enabled, Show DTR bar entry, DTR mode and glyph inputs (`:65`), not a new breeding goal editor.
- `PopupCaptureWindow.cs:32` names Start/Stop Retirement, Start/Stop Covering Selector, Start/Stop Fledgling Selector, Start/Stop Adoption and Open Folder; these remain diagnostic controls.

Selected supporting surfaces are Settings/Appearance and an explicit stock-cleanup preview inset. Popup Capture and its native selectors retain existing diagnostic layouts and visibility; they are not normal end-user breeding pages. No audited Mini window exists.

## Existing UI source inventory

- `ChokeAbo/Windows/ConfigWindow.cs`
- `ChokeAbo/Windows/MainWindow.cs`
- `ChokeAbo/Windows/PopupCaptureWindow.cs`

## Shared appearance contract

Auto and Picker retain their order and values; LCARS is the third option. Preserve native IDs, original actions, saved scope and semantic status colours. The proposed LCARS UI tones default off; the speaker and Settings preference are the same value, with no added volume or pack selector. Krangle obfuscates identifying text. Actual adoption waits for the FrenRider pilot.

Exact generation prompts are in [PROMPTS.md](PROMPTS.md).




