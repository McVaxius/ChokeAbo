# Choke-abo

---

**Help fund my AI overlords' coffee addiction so they can keep generating more plugins instead of taking over the world**

[☕ Support development on Ko-fi](https://ko-fi.com/mcvaxius)

[XA and I have created some Plugins and Guides here at -> aethertek.io](https://aethertek.io/)
### Repo URL:
```
https://aethertek.io/x.json
```

---

Dalamud plugin workspace for `Choke-abo`.

## Interface

Regular and compact windows share the saved colour and language choice. The interface and displayed authored statuses support English, German, French, Spanish, Italian, Russian, Japanese, Korean, Simplified Chinese, Vietnamese, Brazilian Portuguese, Indonesian, Polish and Turkish. Game-sourced names, commands and raw diagnostic details retain their original values.

## Current Status

Stage 1 target-pedigree automation is implemented and builds in `Debug x64`. Inventory decoding, deterministic planning, per-character lifecycle persistence, adaptive feeding, V1/V2 IPC, and the passive dialog recorder are present. Exact retirement, covering, fledgling-selection, and adoption actions remain safely blocked until the four recorder captures provide their real addon mappings; this has not been accepted live in game.

- Solution: `Z:\ChokeAbo\ChokeAbo.sln`
- Project: `Z:\ChokeAbo\ChokeAbo\ChokeAbo.csproj`
- Command: `/chokeabo`
- Capture recorder: `/chokeabo dpopup`
- Repository target: `Public`

## Target-pedigree workflow

Hindi uses installed shaping fonts. The source now leaves other languages usable when the Hindi menu caption is unavailable, showing a disabled ASCII `Hindi (unavailable)` option. Required text for a selected Hindi UI still requires full validation; on failure, the readable status offers **Use English**, saving English only after an explicit press. Native verification and Linux/Wine acceptance remain pending for this change.

Settings owns shared colour, UI language, compact spacing and window transparency/fade; optional Main selectors change the same saved preferences. Main branding and expanded/collapsed titles use the packaged Choke-abo icon in its original colours. Progression Resume/Pause/Stop remains separate from manual Run Full Cycle/Stop, and VERMAXION remains the target-pedigree settings owner.

- VERMAXION remains the settings owner and defaults to Always Race. Its optional Target Pedigree mode supplies pedigree G2-G9, retirement rank 40-50, and preferred feed grade 1-3 over strict V2 JSON IPC.
- Choke-abo selects exact inventory forms by useful pedigree/sex, remaining capacity, container, and slot. It persists action intent before purchases or feeding and yields racing only when the current plan permits it.
- Target feeding spends one confirmed stable session per adaptive round, consumes stocked preferred-grade feed first, and can fall back to Grade 1 gil feed when higher-grade MGP is exhausted.
- Covering uses a 24-hour eligibility wait. Legacy covering state is migrated once without changing the configuration version.

### Manual stock cleanup

Breeding stock includes **Clean up G1-G8 stock** after G9 is reached and breeding actions have settled. Its preview selects fledglings and retired registrations; purchased covering permissions are optional and initially unchecked. Exclude individual stacks, then confirm with **Discard selected stock**. Discard is permanent. G9 stock, covering proof and selected breeding inputs remain protected. The batch rechecks each exact item before dispatch and stops on changed or unreadable state; its result shows confirmed removal and any partial stop. **Cancel cleanup** stops further dispatches but cannot undo a discard already sent.

## Dialog capture handoff

Run `/chokeabo dpopup` and capture each workflow manually with multiple eligible forms visible. The window permits one active capture and contains Start/Stop buttons for Retirement, Covering Selector, Fledgling Selector, and Adoption plus Open Folder. It appends UTF-8 sessions to `retirement.md`, `cselector.md`, `fselector.md`, and `adoption.md` in Choke-abo's plugin configuration directory. The recorder is passive: it refuses to start while Choke-abo automation is running and never operates or stops the client.

## Documents

- Project plan: `Z:\xa-xiv-docs\Dhog\ChokeAbo\CHOKEABO_PROJECT_PLAN.md`
- Knowledge base: `Z:\xa-xiv-docs\Dhog\ChokeAbo\CHOKEABO_KNOWLEDGE_BASE.md`
- Import guide: `how to import plugins.md`
- Changelog: `CHANGELOG.md`

## Notes

- Icon assets live in `images\iconHQ.png` and `images\icon.png`.
- SamplePlugin references used for the initial shell: https://github.com/goatcorp/SamplePlugin and https://github.com/goatcorp/SamplePlugin/blob/master/README.md

## Support logs

Use **Copy / ZIP Dalamud log** in Settings > Settings to create a local ZIP and open its folder. At 100 MiB or above, the first click warns that logging may have stopped and recent activity may be missing; click **Export capped log anyway** only if you still want that snapshot. Share the ZIP manually and remove exports when no longer needed. **Open Export Folder** reopens the completed export’s folder.

When XA Slave is loaded, **Open XA Slave log tools** opens its **Utility > XA Mods** panel, which contains Dalamud Log Cleaner. The existing **Copy / ZIP Dalamud log** action remains separate. Opening the panel does not run cleanup or change XA Slave settings.

Compact mode defaults on. The main Compact and Transparency controls start hidden; Appearance settings keeps density, transparency and independent main-control visibility choices. The one-time migration preserves opacity and unrelated preferences, and later loads retain your choices.
