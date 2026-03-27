using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ChokeAbo.Services;

namespace ChokeAbo.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly ChocoboStatsService chocoboStatsService;

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

    public override void OnOpen()
    {
        chocoboStatsService.RequestRefresh();
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

        ImGui.TextWrapped("Live racing chocobo data, a compact training plan, and the first staged buy/feed automation pass. For now, stand near the Race Counter NPCs before running automation.");
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

    private void DrawCompactWorkspace(Configuration cfg, ChocoboTrainingSnapshot snapshot, ChocoboTrainingSnapshot projected, FeedPurchasePlan purchasePlan, int plannedSessions)
    {
        if (ImGui.BeginTable("ChokeAboCompactWorkspace", 2, ImGuiTableFlags.SizingStretchProp))
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

        if (ImGui.BeginTable("ChokeAboCurrentAndProjected", 3, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Stat");
            ImGui.TableSetupColumn("Now", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("After", ImGuiTableColumnFlags.WidthFixed, 80f);
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
        ImGui.TextWrapped("Qty per stat, then G1/G2/G3. Current and projected stay on the left so this side can stay compact.");

        var changed = false;
        if (ImGui.BeginTable("ChokeAboPlanTable", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("Stat", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("G1", ImGuiTableColumnFlags.WidthFixed, 32f);
            ImGui.TableSetupColumn("G2", ImGuiTableColumnFlags.WidthFixed, 32f);
            ImGui.TableSetupColumn("G3", ImGuiTableColumnFlags.WidthFixed, 32f);
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
        if (ImGui.Button("Stop", new Vector2(80f, 28f)))
            plugin.StopAutomation();

        ImGui.TextDisabled("Automation uses the session-capped plan and subtracts feed already on hand before buying.");
        ImGui.Text($"Buy status: {plugin.VendorPurchaseService.StatusText}");
        ImGui.Text($"Feed status: {plugin.StableFeedingService.StatusText}");
    }

    private static void DrawStatRow(string label, ChocoboStatSnapshot stat)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted(stat.Current.ToString());
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted(stat.Maximum.ToString());
    }

    private static void DrawProjectionRow(string label, ChocoboStatSnapshot current, ChocoboStatSnapshot projected)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted(label);
        ImGui.TableSetColumnIndex(1);
        ImGui.TextUnformatted($"{current.Current}/{current.Maximum}");
        ImGui.TableSetColumnIndex(2);

        if (projected.Current > current.Current)
        {
            ImGui.TextColored(new Vector4(0.42f, 1.0f, 0.56f, 1.0f), $"{projected.Current}/{projected.Maximum}");
        }
        else
        {
            ImGui.TextUnformatted($"{projected.Current}/{projected.Maximum}");
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

        return $"Sessions {snapshot.SessionsAvailable}, plan {plannedSessions}, speed {snapshot.MaximumSpeed.Current}/{snapshot.MaximumSpeed.Maximum}, acceleration {snapshot.Acceleration.Current}/{snapshot.Acceleration.Maximum}, endurance {snapshot.Endurance.Current}/{snapshot.Endurance.Maximum}, stamina {snapshot.Stamina.Current}/{snapshot.Stamina.Maximum}, cunning {snapshot.Cunning.Current}/{snapshot.Cunning.Maximum}.";
    }
}
