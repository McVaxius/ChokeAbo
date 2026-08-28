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

## Current Status

Stage 1 target-pedigree automation is implemented and builds in `Debug x64`. Inventory decoding, deterministic planning, per-character lifecycle persistence, adaptive feeding, V1/V2 IPC, and the passive dialog recorder are present. Exact retirement, covering, fledgling-selection, and adoption actions remain safely blocked until the four recorder captures provide their real addon mappings; this has not been accepted live in game.

- Solution: `Z:\ChokeAbo\ChokeAbo.sln`
- Project: `Z:\ChokeAbo\ChokeAbo\ChokeAbo.csproj`
- Command: `/chokeabo`
- Capture recorder: `/chokeabo dpopup`
- Repository target: `Public`

## Target-pedigree workflow

- VERMAXION remains the settings owner and defaults to Always Race. Its optional Target Pedigree mode supplies pedigree G2-G9, retirement rank 40-50, and preferred feed grade 1-3 over strict V2 JSON IPC.
- Choke-abo selects exact inventory forms by useful pedigree/sex, remaining capacity, container, and slot. It persists action intent before purchases or feeding and yields racing only when the current plan permits it.
- Target feeding spends one confirmed stable session per adaptive round, consumes stocked preferred-grade feed first, and can fall back to Grade 1 gil feed when higher-grade MGP is exhausted.
- Covering uses a 24-hour eligibility wait. Legacy covering state is migrated once without changing the configuration version.

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
