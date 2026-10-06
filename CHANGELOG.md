# Changelog

## 2026-10-06 - Hindi UI integration

- Add the complete 399-entry Hindi catalog through the existing saved-language/resource path. Route translated measurements, window/status text, captions, tooltips, selectors and retained text editors through the shared Windows shaping host while preserving native IDs, automation and saved values.
- Keep game-owned DTR status and tooltip text in English when Hindi is selected; the ImGui shaping host cannot render game SeStrings. Other DTR language choices and configured glyphs retain their existing behavior. Validate each original font role's shaped text before checking the atlas for other scripts. Current-product verification and user-controlled game acceptance remain pending.

## 2026-10-06 - Duty selection inspection

- Include the native highlighted duty and bounded selected-duty entries in the existing Contents Finder inspection, so queue verification failures can be diagnosed without guessing callback mappings.
- Keep native inspection on the existing command path; remove the temporary constructor capture that accessed game objects outside the framework thread.

## 2026-10-06 - Window appearance and transparency

- Move colour configuration into the existing Settings Window appearance section, retaining compact/language access there. Independently hide or show the main-window compact and language controls, both visible by default. Add the main Transparency toggle without changing automation actions or native control identities.
- Persist full-window opacity through the existing configuration: 100% normal opacity, automatic fade enabled, 50% unfocused opacity after 10 seconds. Clamp opacity to 10-100% and delay to 0-3600 seconds. Apply one shared opacity pass per owner after native drawing and motion restoration, including collapsed and font-status windows; retain native chrome, image alpha and owned child/pop-up content.
- Translate the eight new appearance labels in all 14 existing catalogs. Current-version compilation, focused persistence/focus checks and game acceptance remain pending for this source change.

## 2026-10-05 - Rounded outer window chrome

- Adopt shared rounded chrome and native minimize in Main, Settings, capture and font status, preserving native IDs, constraints, saved geometry and actions. Native and game verification remain pending.

## 2026-10-05 - Readable retained windows

- Include the selected UI culture's number-group separator in the existing font requirements so grouped offspring, XP, currency and purchase totals retain their proper spacing. Keep original font roles, sources and merge order.
- Match retained regular/compact controls to the approved 52/48-pixel heights, add rounded gradient card borders and the progression summary, and size borrowed breeding-stock artwork at 36/32 pixels. Keep original control IDs, keyboard focus, callbacks and automation behavior.
- Measure translated breeding-stock and purchase-plan headings, retain horizontal access, and keep title/header glyph bearings and descenders inside their native clips. Measure the capture popup before native Begin so translated controls remain readable.
- Use the existing generated status-template matcher without backtracking to prevent rendering-time match timeouts. Initialize matching templates only when their authored prefix is reached, reducing unused matcher allocation. Retain template order, timeout, captured values and nested authored translation; all fourteen catalogs pass the comparison against the prior engine.

## 2026-10-04 - Permit purchase cleanup

- Keep the verified purchase receipt when the currency shop reappears after an asynchronous purchase refresh. Use the existing native action cadence and owned-shop checks to finish closing it, then require event release before covering. Never replay the retained permit purchase.

## 2026-10-04 - Additional interface languages

- Add Vietnamese, Brazilian Portuguese, Indonesian, Polish and Turkish to the existing language selector and embedded interface/status catalogs. Retain the original nine choices and the existing configuration, DTR, command and automation behavior.
- Include the added resource text and native selector names in the existing required-glyph route. Whole-window reference and managed-host/game acceptance remain pending.

## 2026-10-03 - AethertekUI presentation

- Show the current assembly version in the main title while retaining the saved window ID. Keep translated controls on one line, reflow whole actions, retain readable field/training-column minima and horizontal access, and restore filled support artwork and the progression header action placement.
- Clip translated native field labels and measure their layout bounds without changing DTR/configuration or training input IDs. Preserve the purchase callback command as raw text in its localized diagnostic wrapper.
- Apply the approved regular and compact Choke-abo layouts: branded header, progression and breeding-stock cards, retained manual planner, and separate capture controls. Share compact density across the existing windows and preserve their native control identities and automation actions.
- Add nine embedded UI languages, translated service-status copies and DTR text, managed Latin/Cyrillic/CJK/symbol fonts, and relative whole-theme colour selection. Reuse the existing configuration save path without changing its version or resetting settings.
- Measure translated controls and wrap action rows, retain game-sourced names and command/log text, and reference the shared AethertekUI project. Use typed interpolation for selected-locale UI numbers and check the captured font generation before accepting glyph coverage. Existing launcher Debug and direct Release builds passed locally; the package contains all nine 391-entry resources and AethertekUI 0.3.0. Host glyph readiness and game visual acceptance remain pending; those launcher checks did not run tests or clients.
- Apply inherited UI alpha once when painting native button and checkbox labels; retain their original IDs, callbacks and configuration writes. The final source review preserves progression build marker 95 and the combined permit addon/agent cleanup.


## 2026-10-02 - Build and release repair

- Pin GitHub builds to SDK 10.0.201 and pass the downloaded Dalamud library path. Restore and build plugin projects with matching configuration, platform and runtime; stop on restore failure.
- Keep build tokens read-only and release writes in a separate job. Use packaged manifest versions for untagged releases.
- Local launchers build the plugin directly in the pinned environment and return its exit status.

- Release the verified permit vendor's addon and shop agent together after inventory confirms receipt. Keep purchase evidence until the interaction releases; a failed cleanup still pauses for reconciliation instead of purchasing again. Fresh combined cleanup acceptance is pending the next permit transaction.

- Build only the plugin project in GitHub Actions so test and regression projects do not block production artifacts.

- Add separate V3 workflow operations for pedigree-9 ability printing and colour seeking, with saved independent match counts and requested quantities. Retain matching offspring unregistered, validate each newly collected exact slot before counting, and save counting with collection reconciliation. VERMAXION provides goal selection, quantities, progress, Resume and explicit new batches. Reuse owned or permit covering, reserves and stop controls; permit no lower-generation rebuilding. Live G9 production acceptance remains pending.
- Verify FULL STOP cancels an owned pending race queue and keeps progression paused across a VERMAXION reload. Explicit Resume restores racing with reserves, feeding policy and daily consumption preserved. Remove the temporary development test hooks after native acceptance.
- Show inherited abilities on breeding-stock rows using Lumina names/icons. The combined native fields agree with seven exact form tooltips and the ability read from a freshly registered racer; full G9 production verification remains pending.

- Replace the rejected single-field ability inspection with an explicitly labelled combined-field candidate. Resolve it through Lumina in bounded native inspection; offspring matching remains disabled pending verification.

## Unreleased - progression overview (runtime acceptance pending)

- Put progression, the next action, local covering readiness, retained stock and remaining parent coverings on the main screen. Keep the manual training planner in a collapsible section and stack its panels at narrow widths.
- Distinguish pedigree from racing rank and feed grade. Show reserves and feeding policy, with progression Pause/Resume/Stop routed through VERMAXION's existing controls.
- Open VERMAXION's daily settings directly and show setup guidance before a progression cycle exists, instead of displaying G0 and unset feed/reserve values.
- Read inherited ability, learned ability and feather colour from the native racer structure. Show Lumina ability names, game icons, named colour swatches and inventory item icons; expose optional racer details through compatible IPC status fields. Native offspring-form matching and the new production modes remain unfinished.
- Extend the existing bounded inspector with racer abilities/colour and declared inventory fields for fledgling/retired forms. Record those fields as raw observations for native matching research; do not interpret them as confirmed offspring attributes or add a watcher.
- Include ItemDetail in the existing selected breeding capture and bounded visible-window inspection, so item details can be compared with raw fledgling fields when researching ability/colour matching. No metadata decoding or additional UI callback is inferred.
- Add a bounded native inventory-tooltip inspection for retained and fledgling forms through `/chokeabo inspect forms`. Verify the exact container, slot and item before recording details, close only the owned tooltip, and cancel on Stop, unload or loss of idle character readiness. Offspring matching remains unverified.
- Show retained and fledgling colours with Lumina names and swatches. Validate the current exact inventory slot and use the colour field confirmed against seven observed form tooltips. Reject the tested ability-field candidate because its Lumina names disagreed with those tooltips; ability matching and productive batches remain unfinished. Remove the temporary automatic UI inspection after development readback; native tooltip refresh failures stop the manual inspection explicitly.
- Keep redundant intermediate same-sex fledglings unregistered when a usable parent of that pedigree/sex is already retained. A missing sex, an exhausted parent replacement and the final target racer remain eligible for registration; preserve the reached pedigree floor.
- Add an explicit permit counterpart objective: a retained parent one pedigree below the reached floor plus its opposite-sex permit produces another offspring at that floor. Never rent a lower pair, register a redundant intermediate or lower the final progression target. Use dedicated V3 operations so older builds cannot silently advance instead; purchase re-planning uses the same objective. Runtime verification is pending.

## Unreleased — pedigree progression V3 (runtime acceptance pending)

- Close the verified permit vendor through the native addon-close operation after inventory confirms purchase. Agent hiding alone left the currency-exchange window and vendor event active during a fresh transaction. Keep purchase evidence until the interaction releases, and report a retained-item cleanup timeout specifically.
- Reconcile a retained permit during reload cleanup even while its purchase baseline remains pending, only when the matching item's inventory increase proves receipt. Keep the baseline for the normal purchase-completion check after the window closes.
- Complete retained-permit receipt reconciliation after the shop and vendor event are closed even if Resume cleanup cleared the NPC target. Closing a visible shop still requires the verified supplier and matching active addon.
- Collect through the observed breeder's Fledgling Adoption menu. Save the exact proof and both possible offspring counts before dispatch; require proof consumption and one matching fledgling, and reconcile interrupted collection before retrying. Open the observed Race Chocobo Registration menu before selecting the form and randomized name. Live collection and registration are verified for permit-bred G2 and owned-parent G2/G3 offspring; interrupted collection-resume verification remains pending.
- Buy a missing owned G1 form at the observed Feathertrader through its native shop handler. Verify the item, price and gil reserve before dispatch, persist intent, and require inventory growth before registration. Unresolved purchases remain blocked without replay; the G1 female purchase is live verified.
- Add V3 breeding mode, gil/MGP reserves, explicit Resume, and current pedigree/racing-rank status while retaining V1/V2 endpoints. Older callers cannot downgrade a V3 cycle's spending limits. Reload suspension preserves pending action evidence; executor work requires an active VERMAXION handoff.
- During a missing-counterpart covering wait, allow racing only when a racer is actually registered. Owned G1 covering is live verified: both retained parent capacities decreased and proof appeared without a permit purchase.
- Keep the highest retained pedigree as the progression floor, including exhausted parents. Never register/race a lower-generation offspring, cover a pair that produces below that floor, or restart from G1 after advancing. An owned pair may produce another current-generation parent; missing stock otherwise blocks for explicit permit selection. Intermediate racers retire at rank 40; the target is retained through rank 50.
- Preserve a submitted covering's parent evidence and original collection deadline when changing the next pairing's settings. Collection still verifies the actual submitted pedigree before planning another covering.
- Use stocked preferred-grade feed first, check reserves immediately before purchase/confirmation, and support Fall back, Skip (default), and Stop. Skipping bypasses the current racer/rank feeding round; Stop requires explicit Resume.
- Add `/chokeabo inspect [addon]` using the existing recorder to capture bounded current racer, inventory, visible windows, agent ownership, values, nodes and registered events without operating the UI or adding a new report.
- Load current racer data before planning and distinguish an empty racer from unloaded data. Register an exact fledgling form through native item selection, word-list naming and the owned confirmation; require form consumption plus matching current pedigree/sex. Cancel inventory sessions through their native owner and reconcile interrupted registration or feeding against unchanged item/session state.
- Gate initial feeding on the native racing unlock, approach the registrar's training course through observed menu entries, and hand its queue to VERMAXION. V3 carries the current race-admission allowance so tutorial admission also respects Stop and the daily limit.
- Reassess feeding/racing when returning to the registrar sets the native racing-unlocked flag; stop looking for the initial tutorial-only menu after that state change.
- Refresh unloaded racer data through the observed native Chocobo tab event, reuse an already open Gold Saucer window, and preserve specific refresh errors.
- Release the owned Feathertrader shop through its native shop handler and shop/inventory agents, including a stranded post-purchase interaction. Wait for the native transaction to settle and OccupiedInEvent to clear before registration. Treat OccupiedInEvent as unavailable for travel.
- After a verified permit purchase, close the matching MGP shop through its native agent and wait for the vendor interaction to release before travelling to the breeder. Recover a stranded shop only when the selected permit is already in inventory; preserve it instead of buying again. Live recovery released the vendor event and completed G3 permit covering without another purchase.
- Capture active conditions, teleport action status and Gold Saucer teleport availability in bounded native inspection. Breeding registration travel now dispatches once per action instead of repeating a failed Lifestream trip.
- Dispatch vendor/trainer travel once and wait for Lifestream and player readiness before movement. Release purchase intent after a verified failure before any purchase callback and close a stranded travel destination window during its reload reconciliation.
- Reuse the existing multi-buy planner and vendor queue for all available target training sessions. Subtract stock and check the full batch against reserves; persist each missing item's purchase target and require the full inventory result before feeding.
- Release the owned trainer feeding inventory when the VERMAXION lease ends or either plugin reloads. Preserve its exact item/session evidence for Resume so an interrupted inventory window cannot block world readiness indefinitely. Live recovery released a stranded trainer session, resumed automatically and consumed retained feed without another purchase.
- On explicit Resume, reconcile a blocked feeding attempt only for a known feed item with unchanged inventory/training allowance and a released trainer session. Keep the vendor-to-trainer interaction path usable while blocking travel during OccupiedInEvent.
- Reconcile an interrupted, unconsumed training session when the active owner reconnects after reload; keep explicit Stop paused. Reconcile a late completed feed batch on Resume from its saved item targets.
- Apply the selected feed policy if reserves change before an unsent purchase, retaining any stock already bought. Fall back also considers available Grade1 stock when inventory space or vendor access prevents the preferred batch.
- Randomize both registration name parts from the native naming word list's enabled options. Match the owned confirmation to the two displayed words instead of a fixed name.
- Open the observed retirement menu by its native list entry and recognize the trainer's icon menu while awaiting inspection, preventing repeated NPC interaction.
- Confirm the observed intermediate-racer retirement prompt only at rank40 with room for the retained form. Persist its expected item quantity before dispatch, require both that increase and a cleared racer, and cancel an untouched owned confirmation during Stop/reload cleanup.
- Handle the observed farewell and ability-retention choice through its enabled native list entry. Reconcile a cancelled retirement against the retained racer and unchanged form inventory before resuming.
- Expose required breeding supplies as a separate progression phase. Buy the matching permit through the existing vendor queue after checking the live MGP shop's item, price and reserve; persist the item baseline and require inventory growth. Unverified purchases are not replayed, and required-purchase failures pause independently of optional feeding policy.
- Debug x64 builds use isolated output; release version remains 1.1.0.1. Permit-cycle and owned-parent collection, randomized G2/G3 registration, and intermediate rank-40 retirement have live verification. Interrupted collection recovery and G9/rank-50 acceptance remain outstanding.
- Adopt an interrupted required-permit purchase from its exact native confirmation without replaying the purchase. Match the parent shop, item, quantity and live price, recheck reserves, and require inventory growth after the observed Yes button.
- Reach the breeder on foot and select exact retained stock through the native inventory menu. Verify the game's reserved stock slots and displayed fee before Commence and its owned confirmation; preserve the gil reserve and reconcile Proof of Covering before starting the existing 24-hour wait.

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
