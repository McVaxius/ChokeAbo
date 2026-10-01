using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ChokeAbo.Services;
using Lumina.Excel.Sheets;

namespace ChokeAbo.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ChocoboStatsService chocoboStatsService;
    private TargetCycleStatus? progressionStatus;
    private IReadOnlyList<ChocoboInventoryForm> breedingStock = Array.Empty<ChocoboInventoryForm>();
    private DateTime nextProgressionRefreshUtc;
    private ulong progressionContentId;

    public MainWindow(Plugin plugin, ChocoboStatsService chocoboStatsService)
        : base($"{PluginInfo.DisplayName}##Main")
    {
        this.plugin = plugin;
        this.chocoboStatsService = chocoboStatsService;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640f, 440f),
            MaximumSize = new Vector2(1500f, 1300f),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var cfg = plugin.Configuration;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        var snapshot = chocoboStatsService.Snapshot;
        var projected = chocoboStatsService.BuildProjection(cfg);
        var purchasePlan = chocoboStatsService.BuildPurchasePlan(cfg);
        var plannedSessions = ChocoboStatsService.GetPlannedTrainingCount(cfg);

        ImGui.Text($"{PluginInfo.DisplayName} v{version}");
        ImGui.SameLine(ImGui.GetWindowWidth() - 120f);
        if (ImGui.SmallButton("Ko-fi"))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });

        ImGui.Separator();

        var enabled = cfg.PluginEnabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            cfg.PluginEnabled = enabled;
            cfg.Save();
            plugin.UpdateDtrBar();
        }

        ImGui.SameLine();
        var dtr = cfg.DtrBarEnabled;
        if (ImGui.Checkbox("DTR Bar", ref dtr))
        {
            cfg.DtrBarEnabled = dtr;
            cfg.Save();
            plugin.UpdateDtrBar();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Settings"))
            plugin.ToggleConfigUi();

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh Chocobo Data"))
            chocoboStatsService.RequestRefresh();

        ImGui.SameLine();
        if (ImGui.SmallButton("Status to chat"))
            plugin.PrintStatus(BuildStatusLine(snapshot, plannedSessions));

        DrawProgressionOverview();
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Manual training and feed purchases"))
            return;
        ImGui.TextWrapped("Plan training sessions, use feed already in inventory, and buy the remaining feed in one visit.");
        ImGui.TextColored(new Vector4(0.80f, 0.86f, 1.0f, 1.0f), chocoboStatsService.StatusText);

        if (snapshot.IsLoaded && plannedSessions > snapshot.SessionsAvailable)
        {
            ImGui.TextColored(
                new Vector4(1.0f, 0.70f, 0.35f, 1.0f),
                $"Planned trainings exceed sessions available: {plannedSessions}/{snapshot.SessionsAvailable}. Extra planned trainings will not fit this cycle.");
        }

        ImGui.Separator();
        DrawCompactWorkspace(cfg, snapshot, projected, purchasePlan, plannedSessions);
    }

    private void DrawProgressionOverview()
    {
        var contentId = Plugin.PlayerState.ContentId;
        var now = DateTime.UtcNow;
        if (contentId == 0)
        {
            ImGui.TextDisabled("Log into a character to see chocobo progression.");
            return;
        }
        if (contentId != progressionContentId || now >= nextProgressionRefreshUtc)
        {
            progressionContentId = contentId;
            progressionStatus = plugin.BreedingService.GetTargetCycleStatus(contentId);
            breedingStock = ChocoboInventoryModel.Enumerate(plugin.InventoryService);
            nextProgressionRefreshUtc = now.AddSeconds(1);
        }
        var state = plugin.CharacterStateService.GetCurrent();
        var status = progressionStatus!;
        var snapshot = chocoboStatsService.Snapshot;
        ImGui.TextUnformatted("Chocobo progression");
        ImGui.TextWrapped("VERMAXION owns progression settings and the daily racing allowance. Choke-abo carries out feeding and breeding.");
        ImGui.Spacing();
        if (ImGui.Button("Open VERMAXION settings"))
            Plugin.CommandManager.ProcessCommand("/vmx chocobo settings");
        ImGui.SameLine();
        if (ImGui.Button("Resume##Progression"))
            Plugin.CommandManager.ProcessCommand("/vmx chocobo resume");
        ImGui.SameLine();
        if (ImGui.Button("Pause##Progression"))
            Plugin.CommandManager.ProcessCommand("/vmx chocobo pause");
        ImGui.SameLine();
        if (ImGui.Button("Stop##Progression"))
        {
            if (state.ExecutionOwner == BreedingExecutionOwner.Target)
                Plugin.CommandManager.ProcessCommand("/vmx chocobo stop");
            else
                plugin.StopAutomation();
        }
        ImGui.Separator();
        if (status.RacingRank > 0)
        {
            ImGui.Text($"Registered pedigree: G{status.Pedigree}    Racing rank: {status.RacingRank}/50");
            ImGui.ProgressBar(status.RacingRank / 50f, new Vector2(-1, 0), $"Racing rank {status.RacingRank}/50");
            DrawAbility("Inherited ability", snapshot.InheritedAbilityId);
            DrawAbility("Learned ability", snapshot.LearnedAbilityId);
            if (Plugin.DataManager.GetExcelSheet<Stain>().TryGetRow(snapshot.ColourId, out var colour))
            {
                DrawColourSwatch(colour.Color);
                ImGui.SameLine();
                ImGui.TextUnformatted($"Colour: {colour.Name.ExtractText()}");
            }
        }
        else
            ImGui.TextDisabled(snapshot.IsLoaded ? "No registered racing chocobo." : "Registered racer data is unavailable.");
        if (state.ExecutionOwner == BreedingExecutionOwner.Target && state.TargetPedigree >= 2)
        {
            if (state.OffspringGoal == OffspringGoal.ReachPedigree)
                ImGui.Text($"Goal: pedigree G{state.TargetPedigree}, racing rank 50");
            else
            {
                ImGui.TextUnformatted(state.OffspringGoal == OffspringGoal.AbilityOffspring ? "Goal: offspring printer" : "Goal: colour seeker");
                ImGui.Text($"Matching G9 offspring: {status.MatchingOffspringProduced:N0} / {status.MatchingOffspringRequested:N0}");
                ImGui.ProgressBar(status.MatchingOffspringRequested > 0
                    ? Math.Clamp((float)status.MatchingOffspringProduced / status.MatchingOffspringRequested, 0, 1) : 0,
                    new Vector2(-1, 0), "Matching offspring retained unregistered");
                if (state.OffspringGoal == OffspringGoal.AbilityOffspring)
                    DrawAbility("Desired inherited ability", state.DesiredInheritedAbilityId);
                else
                    foreach (var colourId in state.AcceptableColourIds)
                        if (Plugin.DataManager.GetExcelSheet<Stain>().TryGetRow(colourId, out var acceptableColour))
                        {
                            DrawColourSwatch(acceptableColour.Color);
                            ImGui.SameLine();
                            ImGui.TextUnformatted(acceptableColour.Name.ExtractText());
                        }
            }
            ImGui.Text($"Breeding mode: {(state.BreedingMode == BreedingMode.OwnedParents ? "Owned parents" : "NPC covering permits")}");
            if (state.BreedingMode == BreedingMode.NpcPermits)
                ImGui.TextDisabled(state.OffspringGoal != OffspringGoal.ReachPedigree ? "Permit objective: produce G9 offspring"
                    : state.ProduceCounterpart ? "Permit objective: produce a missing current-pedigree counterpart" : "Permit objective: advance pedigree");
            ImGui.Text($"Reserves: {state.GilReserve:N0} gil    {state.MgpReserve:N0} MGP");
            var feedPolicy = state.FeedPolicy switch
            {
                InsufficientFeedPolicy.FallBack => "Fall back to Grade 1, then skip",
                InsufficientFeedPolicy.Stop => "Stop until Resume",
                _ => "Skip this feeding round",
            };
            ImGui.Text($"Feed: Grade {state.PreferredFeedGrade}    If unavailable: {feedPolicy}");
        }
        else
            ImGui.TextWrapped("Choose your progression goal, reserves and feeding policy in VERMAXION settings, then Resume to begin.");
        ImGui.TextUnformatted(state.Phase switch
        {
            BreedingPhase.CoveringWait => "Covering in progress",
            BreedingPhase.AdoptionPendingCapture => "Collecting offspring",
            BreedingPhase.RegistrationPendingCapture => "Registering offspring",
            BreedingPhase.RetirementPendingCapture => state.OffspringGoal == OffspringGoal.ReachPedigree ? "Retiring the intermediate racer" : "Retiring the G9 breeding parent",
            BreedingPhase.CoveringPendingCapture => "Preparing covering",
            BreedingPhase.Paused => "Paused - Resume is required",
            BreedingPhase.Blocked => "Needs attention",
            BreedingPhase.TargetReady => status.ProductionComplete ? "Offspring goal reached" : status.ProgressionComplete ? "Progression complete" : "Verifying the registered racer",
            BreedingPhase.Racing => "Ready for racing",
            BreedingPhase.Feeding => "Feeding the racer",
            BreedingPhase.PurchasingFeed or BreedingPhase.PurchasingSupplies => "Purchasing supplies",
            _ => "Ready to plan the next action",
        });
        ImGui.TextWrapped($"Next action: {status.Reason}");
        var coveringAt = state.CoveringEligibleAtUtc;
        if ((state.Phase == BreedingPhase.CoveringWait || state.PhaseBeforePause == BreedingPhase.CoveringWait && state.Phase == BreedingPhase.Paused) && coveringAt != DateTime.MinValue)
        {
            var remaining = coveringAt.ToUniversalTime() - now;
            ImGui.TextUnformatted(remaining > TimeSpan.Zero
                ? $"Collection ready in {remaining:hh\\:mm\\:ss} - {coveringAt.ToLocalTime():ddd, MMM d HH:mm} local"
                : "Covering is ready for collection.");
        }
        ImGui.Spacing();
        ImGui.TextUnformatted("Breeding stock");
        ImGui.TextDisabled("Offspring sex is random. Two coverings do not guarantee a male and a female.");
        if (ImGui.BeginTable("BreedingStock", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Pedigree / sex", ImGuiTableColumnFlags.WidthFixed, 125);
            ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("Coverings left", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();
            foreach (var form in breedingStock)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                if (Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(form.ItemId, out var item))
                {
                    DrawGameIcon(item.Icon);
                    ImGui.SameLine();
                }
                ImGui.TextWrapped(form.ItemName);
                if (ChocoboInventoryModel.TryReadInheritedAbility(form, out var stockAbilityId))
                {
                    var stockAbility = Plugin.DataManager.GetExcelSheet<ChocoboRaceAbility>().GetRow(stockAbilityId);
                    DrawGameIcon(stockAbility.Icon);
                    ImGui.SameLine();
                    ImGui.TextUnformatted(stockAbility.Name.ExtractText());
                }
                if (ChocoboInventoryModel.TryReadColour(form, out var colourId))
                {
                    var colour = Plugin.DataManager.GetExcelSheet<Stain>().GetRow(colourId);
                    ImGui.PushID($"StockColour{form.Container}{form.Slot}");
                    DrawColourSwatch(colour.Color);
                    ImGui.PopID();
                    ImGui.SameLine();
                    ImGui.TextUnformatted(colour.Name.ExtractText());
                }
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(form.Pedigree > 0 ? $"G{form.Pedigree} {form.Sex}" : "Covering proof");
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(form.Quantity.ToString());
                ImGui.TableSetColumnIndex(3);
                ImGui.TextUnformatted(form.Kind == ChocoboFormKind.Retired ? form.Capacity.ToString() : "-");
            }
            ImGui.EndTable();
        }
        if (breedingStock.Count == 0)
            ImGui.TextDisabled("No breeding forms or covering proof in inventory.");
    }

    private static void DrawAbility(string label, uint abilityId)
    {
        if (abilityId != 0 && Plugin.DataManager.GetExcelSheet<ChocoboRaceAbility>().TryGetRow(abilityId, out var ability))
        {
            DrawGameIcon(ability.Icon);
            ImGui.SameLine();
            ImGui.TextUnformatted($"{label}: {ability.Name.ExtractText()}");
        }
        else
            ImGui.TextDisabled($"{label}: none");
    }

    private static void DrawGameIcon(uint iconId)
    {
        var texture = Plugin.TextureProvider.GetFromGameIcon(iconId).GetWrapOrDefault();
        if (texture != null)
            ImGui.Image(texture.Handle, new Vector2(24, 24));
    }

    private static void DrawColourSwatch(uint rgb)
        => ImGui.ColorButton("##RacerColour", new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1),
            ImGuiColorEditFlags.NoTooltip, new Vector2(24, 24));

    private void DrawCompactWorkspace(Configuration cfg, ChocoboTrainingSnapshot snapshot, ChocoboTrainingSnapshot projected, FeedPurchasePlan purchasePlan, int plannedSessions)
    {
        if (ImGui.GetContentRegionAvail().X < 900)
        {
            DrawOverviewPanel(snapshot, projected, purchasePlan, plannedSessions);
            ImGui.Spacing();
            DrawPlanEditor(cfg, snapshot, projected);
        }
        else if (ImGui.BeginTable("ChokeAboCompactWorkspace", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Overview", ImGuiTableColumnFlags.WidthStretch, 0.52f);
            ImGui.TableSetupColumn("Plan", ImGuiTableColumnFlags.WidthStretch, 0.48f);
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawOverviewPanel(snapshot, projected, purchasePlan, plannedSessions);

            ImGui.TableSetColumnIndex(1);
            DrawPlanEditor(cfg, snapshot, projected);
            ImGui.EndTable();
        }

        ImGui.Spacing();
        DrawPurchasePlan(snapshot, purchasePlan);
    }

    private void DrawOverviewPanel(ChocoboTrainingSnapshot snapshot, ChocoboTrainingSnapshot projected, FeedPurchasePlan purchasePlan, int plannedSessions)
    {
        ImGui.TextUnformatted("Overview");
        if (!snapshot.IsLoaded)
        {
            ImGui.TextDisabled("No chocobo data loaded yet.");
            return;
        }

        if (ImGui.BeginTable("ChokeAboOverviewSummary", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text($"Racing rank: {snapshot.Rank}/50");
            ImGui.Text($"Rating: {snapshot.Rating}");
            ImGui.Text($"Pedigree: G{snapshot.PedigreeLevel}");
            ImGui.Text($"XP: {snapshot.ExperienceCurrent:N0}/{snapshot.ExperienceMax:N0}");
            ImGui.Text($"Sessions: {snapshot.SessionsAvailable}");
            ImGui.Text($"Plan: {plannedSessions}");

            ImGui.TableSetColumnIndex(1);
            ImGui.Text($"Gil: {purchasePlan.CurrentGil:N0}");
            ImGui.Text($"MGP: {purchasePlan.CurrentMgp:N0}");
            ImGui.EndTable();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Source))
            ImGui.TextDisabled(snapshot.Source);

        var gilColor = purchasePlan.TotalGil == 0 || purchasePlan.CanAffordGil
            ? new Vector4(0.42f, 1.0f, 0.56f, 1.0f)
            : new Vector4(1.0f, 0.58f, 0.58f, 1.0f);
        var mgpColor = purchasePlan.TotalMgp == 0 || purchasePlan.CanAffordMgp
            ? new Vector4(0.42f, 1.0f, 0.56f, 1.0f)
            : new Vector4(1.0f, 0.58f, 0.58f, 1.0f);
        ImGui.TextColored(gilColor, $"Need gil: {purchasePlan.TotalGil:N0}");
        ImGui.SameLine();
        ImGui.TextColored(mgpColor, $"Need MGP: {purchasePlan.TotalMgp:N0}");

        if (ImGui.BeginTable("ChokeAboCurrentAndProjected", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Stat");
            ImGui.TableSetupColumn("Stars", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("Now", ImGuiTableColumnFlags.WidthFixed, 88f);
            ImGui.TableSetupColumn("After", ImGuiTableColumnFlags.WidthFixed, 88f);
            ImGui.TableHeadersRow();

            DrawProjectionRow("Maximum Speed", snapshot.MaximumSpeed, projected.MaximumSpeed);
            DrawProjectionRow("Acceleration", snapshot.Acceleration, projected.Acceleration);
            DrawProjectionRow("Endurance", snapshot.Endurance, projected.Endurance);
            DrawProjectionRow("Stamina", snapshot.Stamina, projected.Stamina);
            DrawProjectionRow("Cunning", snapshot.Cunning, projected.Cunning);
            ImGui.EndTable();
        }
    }

    private void DrawPlanEditor(Configuration cfg, ChocoboTrainingSnapshot snapshot, ChocoboTrainingSnapshot projected)
    {
        ImGui.TextUnformatted("Training Plan");
        ImGui.TextWrapped("Choose the number of training sessions and feed grade for each stat.");

        var changed = false;
        if (ImGui.BeginTable("ChokeAboPlanTable", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Stat", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Grade 1", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Grade 2", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("Grade 3", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            var plannedMaximumSpeed = cfg.PlannedMaximumSpeedTrainings;
            var maximumSpeedGrade = cfg.MaximumSpeedFeedGrade;
            changed |= DrawPlanRow("Maximum Speed", ref plannedMaximumSpeed, ref maximumSpeedGrade, snapshot.MaximumSpeed, projected.MaximumSpeed, "MaximumSpeed");
            cfg.PlannedMaximumSpeedTrainings = plannedMaximumSpeed;
            cfg.MaximumSpeedFeedGrade = maximumSpeedGrade;

            var plannedAcceleration = cfg.PlannedAccelerationTrainings;
            var accelerationGrade = cfg.AccelerationFeedGrade;
            changed |= DrawPlanRow("Acceleration", ref plannedAcceleration, ref accelerationGrade, snapshot.Acceleration, projected.Acceleration, "Acceleration");
            cfg.PlannedAccelerationTrainings = plannedAcceleration;
            cfg.AccelerationFeedGrade = accelerationGrade;

            var plannedEndurance = cfg.PlannedEnduranceTrainings;
            var enduranceGrade = cfg.EnduranceFeedGrade;
            changed |= DrawPlanRow("Endurance", ref plannedEndurance, ref enduranceGrade, snapshot.Endurance, projected.Endurance, "Endurance");
            cfg.PlannedEnduranceTrainings = plannedEndurance;
            cfg.EnduranceFeedGrade = enduranceGrade;

            var plannedStamina = cfg.PlannedStaminaTrainings;
            var staminaGrade = cfg.StaminaFeedGrade;
            changed |= DrawPlanRow("Stamina", ref plannedStamina, ref staminaGrade, snapshot.Stamina, projected.Stamina, "Stamina");
            cfg.PlannedStaminaTrainings = plannedStamina;
            cfg.StaminaFeedGrade = staminaGrade;

            var plannedCunning = cfg.PlannedCunningTrainings;
            var cunningGrade = cfg.CunningFeedGrade;
            changed |= DrawPlanRow("Cunning", ref plannedCunning, ref cunningGrade, snapshot.Cunning, projected.Cunning, "Cunning");
            cfg.PlannedCunningTrainings = plannedCunning;
            cfg.CunningFeedGrade = cunningGrade;

            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.Button("Clear Plan", new Vector2(110f, 26f)))
        {
            plugin.ClearPlan();
            return;
        }

        if (changed)
            cfg.Save();

        var plannedSessions = ChocoboStatsService.GetPlannedTrainingCount(cfg);
        ImGui.Text($"Planned trainings this cycle: {plannedSessions}");
        if (snapshot.SessionsAvailable > 0)
            ImGui.Text($"Session cap: {snapshot.SessionsAvailable}");
    }

    private void DrawPurchasePlan(ChocoboTrainingSnapshot snapshot, FeedPurchasePlan purchasePlan)
    {
        ImGui.TextUnformatted("Purchase Plan");
        ImGui.TextWrapped("Exact shopping list for the current plan.");

        if (!purchasePlan.IsLoaded)
        {
            ImGui.TextDisabled("Load racing chocobo data first so the purchase plan can compare against sessions available.");
        }
        else if (purchasePlan.PlannedTrainings > purchasePlan.SessionsAvailable)
        {
            ImGui.TextColored(
                new Vector4(1.0f, 0.70f, 0.35f, 1.0f),
                $"Plan exceeds sessions available: {purchasePlan.PlannedTrainings}/{purchasePlan.SessionsAvailable}. The shopping list below still reflects the full plan.");
        }

        if (ImGui.BeginTable("ChokeAboPurchasePlanTable", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Feed");
            ImGui.TableSetupColumn("Plan", ImGuiTableColumnFlags.WidthFixed, 44f);
            ImGui.TableSetupColumn("Qty on hand", ImGuiTableColumnFlags.WidthFixed, 88f);
            ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthFixed, 72f);
            ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 86f);
            ImGui.TableHeadersRow();

            if (purchasePlan.Entries.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextDisabled("No planned feed purchases.");
            }
            else
            {
                foreach (var entry in purchasePlan.Entries)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextWrapped(entry.FeedName);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(entry.PlannedQuantity.ToString());
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(entry.OnHandQuantity.ToString());
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(entry.CurrencyKind == FeedCurrencyKind.Gil ? "gil" : "MGP");
                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextUnformatted($"{entry.TotalCost:N0}");
                }
            }

            ImGui.EndTable();
        }

        var gilColor = purchasePlan.TotalGil == 0 || purchasePlan.CanAffordGil
            ? new Vector4(0.42f, 1.0f, 0.56f, 1.0f)
            : new Vector4(1.0f, 0.58f, 0.58f, 1.0f);
        var mgpColor = purchasePlan.TotalMgp == 0 || purchasePlan.CanAffordMgp
            ? new Vector4(0.42f, 1.0f, 0.56f, 1.0f)
            : new Vector4(1.0f, 0.58f, 0.58f, 1.0f);

        ImGui.TextColored(gilColor, $"Total gil needed: {purchasePlan.TotalGil:N0}");
        ImGui.SameLine();
        ImGui.TextColored(mgpColor, $"Total MGP needed: {purchasePlan.TotalMgp:N0}");

        if (!purchasePlan.CanAffordGil || !purchasePlan.CanAffordMgp)
        {
            ImGui.TextColored(
                new Vector4(1.0f, 0.70f, 0.35f, 1.0f),
                "Current currency is below the planned purchase total. Adjust the plan or stock up before the buy loop is enabled.");
        }

        ImGui.Spacing();
        if (ImGui.Button("Buy Needed Feed", new Vector2(140f, 28f)))
            plugin.StartBuyOnly();

        ImGui.SameLine();
        if (ImGui.Button("Feed Planned Sessions", new Vector2(160f, 28f)))
            plugin.StartFeedOnly();

        ImGui.SameLine();
        if (ImGui.Button("Run Full Cycle", new Vector2(130f, 28f)))
            plugin.StartFullCycle();

        ImGui.SameLine();
        if (ImGui.Button("Start / Resume Breeding", new Vector2(170f, 28f)))
            plugin.StartBreeding();

        ImGui.SameLine();
        if (ImGui.Button("Stop", new Vector2(80f, 28f)))
            plugin.StopAutomation();

        ImGui.TextDisabled("Automation uses the session-capped plan and subtracts feed already on hand before buying.");
        ImGui.Text($"Cleanup status: {plugin.CleanupStatusText}");
        ImGui.Text($"Buy status: {plugin.VendorPurchaseService.StatusText}");
        ImGui.Text($"Feed status: {plugin.StableFeedingService.StatusText}");
        ImGui.Text($"Breeding status: {plugin.BreedingService.StatusText}");
    }

    private static void DrawStatRow(string label, ChocoboStatSnapshot stat)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(FormatStat(stat.Current));
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(FormatStat(stat.Maximum));
    }

    private static void DrawProjectionRow(string label, ChocoboStatSnapshot current, ChocoboStatSnapshot projected)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(FormatStars(current));
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(FormatStatPair(current));
        ImGui.TableSetColumnIndex(3);

        if (projected.Current > current.Current)
        {
            ImGui.TextColored(new Vector4(0.42f, 1.0f, 0.56f, 1.0f), FormatStatPair(projected));
        }
        else
        {
            ImGui.TextUnformatted(FormatStatPair(projected));
        }
    }

    private static bool DrawPlanInput(string label, ref int value)
    {
        var local = value;
        if (!ImGui.InputInt(label, ref local))
            return false;

        value = Math.Max(0, local);
        return true;
    }

    private static bool DrawPlanRow(
        string label,
        ref int plannedTrainings,
        ref int selectedGrade,
        ChocoboStatSnapshot current,
        ChocoboStatSnapshot projected,
        string idPrefix)
    {
        var changed = false;
        selectedGrade = Math.Clamp(selectedGrade, 1, 3);

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);

        ImGui.TableSetColumnIndex(1);
        ImGui.SetNextItemWidth(52f);
        changed |= DrawPlanInput($"##Qty{idPrefix}", ref plannedTrainings);

        ImGui.TableSetColumnIndex(2);
        changed |= DrawGradeRadioButton(ref selectedGrade, 1, $"##{idPrefix}G1");

        ImGui.TableSetColumnIndex(3);
        changed |= DrawGradeRadioButton(ref selectedGrade, 2, $"##{idPrefix}G2");

        ImGui.TableSetColumnIndex(4);
        changed |= DrawGradeRadioButton(ref selectedGrade, 3, $"##{idPrefix}G3");

        return changed;
    }

    private static bool DrawGradeRadioButton(ref int selectedGrade, int grade, string id)
    {
        var localSelected = selectedGrade == grade;
        if (!ImGui.RadioButton(id, localSelected))
            return false;

        selectedGrade = grade;
        return true;
    }

    private static string BuildStatusLine(ChocoboTrainingSnapshot snapshot, int plannedSessions)
    {
        if (!snapshot.IsLoaded)
            return $"Chocobo stats unavailable. Planned trainings: {plannedSessions}.";

        return $"Rank {snapshot.Rank}, rating {snapshot.Rating}, pedigree {snapshot.PedigreeLevel}, XP {snapshot.ExperienceCurrent}/{snapshot.ExperienceMax}, sessions {snapshot.SessionsAvailable}, plan {plannedSessions}, speed {FormatStatWithStars(snapshot.MaximumSpeed)}, acceleration {FormatStatWithStars(snapshot.Acceleration)}, endurance {FormatStatWithStars(snapshot.Endurance)}, stamina {FormatStatWithStars(snapshot.Stamina)}, cunning {FormatStatWithStars(snapshot.Cunning)}.";
    }

    private static string FormatStatWithStars(ChocoboStatSnapshot stat)
        => $"{FormatStatPair(stat)} ({FormatStars(stat)})";

    private static string FormatStatPair(ChocoboStatSnapshot stat)
        => $"{FormatStat(stat.Current)}/{FormatStat(stat.Maximum)}";

    private static string FormatStars(ChocoboStatSnapshot stat)
        => stat.Stars == 1 ? "1 star" : $"{stat.Stars} stars";

    private static string FormatStat(decimal value)
        => value.ToString("0.#", CultureInfo.InvariantCulture);
}
