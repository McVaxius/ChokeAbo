using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using ChokeAbo.Services;
using Lumina.Excel.Sheets;
using AethertekUI;
using AethertekUI.Dalamud;
using ChokeAbo.Ui;

namespace ChokeAbo.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly MaterialWindowMotion windowMotion = new();
    private readonly Plugin plugin;
    private readonly ChocoboStatsService chocoboStatsService;
    private TargetCycleStatus? progressionStatus;
    private IReadOnlyList<ChocoboInventoryForm> breedingStock = Array.Empty<ChocoboInventoryForm>();
    private DateTime nextProgressionRefreshUtc;
    private ulong progressionContentId;
    private StockCleanupPreview? stockCleanupPreview;
    private bool[] stockCleanupSelected = [];
    private bool cleanupFledglings = true;
    private bool cleanupRetired = true;
    private bool cleanupPermissions;

    public MainWindow(Plugin plugin, ChocoboStatsService chocoboStatsService)
        : base($"{PluginInfo.DisplayName}##Main")
    {
        this.plugin = plugin;
        this.chocoboStatsService = chocoboStatsService;
        Flags |= ImGuiWindowFlags.HorizontalScrollbar;
        Size = new Vector2(1120, 880);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640f, 440f),
            MaximumSize = new Vector2(1500f, 1300f),
        };
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Cog, Priority = 0, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.ToggleConfigUi(); },
            ShowTooltip = () => MaterialText.SetTooltip(UiText.T("Settings")),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.PlayCircle, Priority = -10, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) RunProgressionCommandFromUi("resume"); },
            ShowTooltip = () => ShowProgressionTitleTooltip("Resume"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Pause, Priority = -20, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) RunProgressionCommandFromUi("pause"); },
            ShowTooltip = () => ShowProgressionTitleTooltip("Pause"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.StopCircle, Priority = -30, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) StopProgressionFromUi(); },
            ShowTooltip = () => ShowProgressionTitleTooltip("Stop"),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Play, Priority = -40, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.StartFullCycle(); },
            ShowTooltip = () => MaterialText.SetTooltip(UiText.T("Run Full Cycle") + "\n" + UiText.T(plugin.CleanupStatusText)),
        });
        TitleBarButtons.Add(new()
        {
            Icon = FontAwesomeIcon.Stop, Priority = -50, IconOffset = new(2, 1),
            Click = button => { if (button == ImGuiMouseButton.Left) plugin.StopAutomation(); },
            ShowTooltip = () => MaterialText.SetTooltip(UiText.T("Stop") + "\n" + UiText.T("Manual training and feed purchases") + "\n" + UiText.T(plugin.CleanupStatusText)),
        });
    }

    public void Dispose()
    {
    }

    public override void PreDraw()
    {
        UiGui.ReserveTitleSpace(this, $"{PluginInfo.DisplayName} v{typeof(Plugin).Assembly.GetName().Version}", 640);
        windowMotion.Prepare(this, reducedMotion: false, roundedCorners: true);
    }

    public override void PostDraw()
    {
        windowMotion.Restore(this);
        UiGui.PaintTitleWithImage(this, PluginInfo.DisplayName + " v" + (typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0"));
    }

    public override void Draw()
    {
        windowMotion.DrawChrome();
        var cfg = plugin.Configuration;
        DrawPresentation(chocoboStatsService.Snapshot, chocoboStatsService.BuildProjection(cfg),
            chocoboStatsService.BuildPurchasePlan(cfg), ChocoboStatsService.GetPlannedTrainingCount(cfg));
    }

    internal void DrawPresentation(ChocoboTrainingSnapshot snapshot, ChocoboTrainingSnapshot projected, FeedPurchasePlan purchasePlan, int plannedSessions)
    {
        var cfg = plugin.Configuration;

        DrawHeader();

        var enabled = cfg.PluginEnabled;
        if (UiGui.Toggle("Enabled", ref enabled))
        {
            cfg.PluginEnabled = enabled;
            cfg.Save();
            plugin.UpdateDtrBar();
        }

        UiGui.SameLineIfFits(UiGui.ToggleWidth("DTR Bar"));
        var dtr = cfg.DtrBarEnabled;
        if (UiGui.Toggle("DTR Bar", ref dtr))
        {
            cfg.DtrBarEnabled = dtr;
            cfg.Save();
            plugin.UpdateDtrBar();
        }

        UiGui.SameLineIfFits(UiGui.ButtonWidth("Settings", MaterialIcon.Settings));
        if (UiGui.Button("Settings", icon: MaterialIcon.Settings))
            plugin.ToggleConfigUi();

        UiGui.SameLineIfFits(UiGui.ButtonWidth("Refresh Chocobo Data", MaterialIcon.Refresh));
        if (UiGui.Button("Refresh Chocobo Data", icon: MaterialIcon.Refresh))
            chocoboStatsService.RequestRefresh();

        UiGui.SameLineIfFits(UiGui.ButtonWidth("Status to chat", MaterialIcon.Info));
        if (UiGui.Button("Status to chat", icon: MaterialIcon.Info))
            plugin.PrintStatus(BuildStatusLine(snapshot, plannedSessions));

        DrawProgressionOverview(snapshot);
        ImGui.Spacing();
        if (!UiGui.CollapsingHeader("Manual training and feed purchases"))
            return;
        UiGui.TextWrapped("Plan training sessions, use feed already in inventory, and buy the remaining feed in one visit.");
        UiGui.TextColored(MaterialTheme.Current.Colors.Secondary, chocoboStatsService.StatusText);

        if (snapshot.IsLoaded && plannedSessions > snapshot.SessionsAvailable)
        {
            UiGui.TextColored(
                new Vector4(1.0f, 0.70f, 0.35f, 1.0f),
                UiText.Interpolated($"Planned trainings exceed sessions available: {plannedSessions}/{snapshot.SessionsAvailable}. Extra planned trainings will not fit this cycle."));
        }

        ImGui.Separator();
        DrawCompactWorkspace(cfg, snapshot, projected, purchasePlan, plannedSessions);
    }

    private void DrawHeader()
    {
        var cfg = plugin.Configuration; var scale = MaterialTheme.Metrics.Scale;
        var start = ImGui.GetCursorScreenPos();
        var window = ImGuiP.GetCurrentWindow();
        var width = Math.Min(ImGui.GetContentRegionAvail().X, window.InnerRect.Max.X - window.InnerRect.Min.X - 2 * window.WindowPadding.X);
        var titleOffset = (cfg.UiCompact ? 56 : 66) * scale;
        ChokePresentation.Brand(start, (cfg.UiCompact ? 44 : 52) * scale);
        ImGui.SetCursorScreenPos(start + new Vector2(titleOffset, 0));
        using (UiText.Font(cfg.UiCompact ? UiFontRole.CompactTitle : UiFontRole.Title)) MaterialText.Text(PluginInfo.DisplayName);
        var titleRight = ImGui.GetItemRectMax().X;
        var titleHeight = ImGui.GetItemRectSize().Y;
        var titleGroupWidth = titleRight - start.X;
        if (cfg.UiCompactVisibleOnMainWindow)
        {
            var compact = cfg.UiCompact;
            ImGui.SetCursorScreenPos(new Vector2(titleRight + 12 * scale, start.Y + Math.Max(0, (titleHeight - ImGui.GetFrameHeight()) * .5f)));
            if (ImGui.Checkbox("C", ref compact)) { cfg.UiCompact = compact; cfg.Save(); }
            if (ImGui.IsItemHovered()) UiGui.SetTooltip("Compact mode");
            titleGroupWidth = ImGui.GetItemRectMax().X - start.X;
        }
        ImGui.SetCursorScreenPos(start + new Vector2(titleOffset, (cfg.UiCompact ? 34 : 40) * scale));
        UiGui.TextColored(MaterialTheme.Current.Colors.OnSurfaceVariant, "FFXIV Dalamud Plugin");
        var subtitleBottom = ImGui.GetItemRectMax().Y;
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var kofiWidth = UiGui.ButtonWidth("Support on Ko-fi", MaterialIcon.Heart);
        var languageWidth = cfg.UiLanguageVisibleOnMainWindow ? plugin.Appearance.LanguageWidth() : 0;
        var opacityWidth = UiGui.CheckboxWidth("Transparency");
        var tools = opacityWidth + kofiWidth + languageWidth + (cfg.UiLanguageVisibleOnMainWindow ? 2 : 1) * gap;
        var right = width >= titleGroupWidth + tools + 24 * scale;
        var rowTop = right ? start.Y + 5 * scale : subtitleBottom + 12 * scale;
        var height = ChokePresentation.ControlHeight * scale;
        ImGui.SetCursorScreenPos(new Vector2(right ? start.X + width - tools : start.X, rowTop + Math.Max(0, (height - ImGui.GetFrameHeight()) * .5f)));
        plugin.Appearance.DrawTransparencyToggle();
        void Next(float itemWidth)
        {
            var x = ImGui.GetItemRectMax().X + gap;
            if (x + itemWidth > start.X + width) { x = start.X; rowTop += height + ImGui.GetStyle().ItemSpacing.Y; }
            ImGui.SetCursorScreenPos(new Vector2(x, rowTop));
        }
        Next(kofiWidth);
        if (UiGui.Button("Ko-fi", new Vector2(0, 0), MaterialIcon.Heart, UiText.T("Support on Ko-fi")))
            Process.Start(new ProcessStartInfo { FileName = PluginInfo.SupportUrl, UseShellExecute = true });
        if (cfg.UiLanguageVisibleOnMainWindow)
        {
            Next(languageWidth);
            plugin.Appearance.DrawLanguageSelector();
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, Math.Max(start.Y + (cfg.UiCompact ? 68 : 82) * scale,
            ImGui.GetItemRectMax().Y + 12 * scale)));
        ImGui.Separator();
    }

    private void RunProgressionCommandFromUi(string action)
    {
        if (Plugin.PlayerState.ContentId != 0)
            Plugin.CommandManager.ProcessCommand("/vmx chocobo " + action);
    }

    private void StopProgressionFromUi()
    {
        if (Plugin.PlayerState.ContentId == 0) return;
        if (plugin.CharacterStateService.GetCurrent().ExecutionOwner == BreedingExecutionOwner.Target)
            RunProgressionCommandFromUi("stop");
        else plugin.StopAutomation();
    }

    private void ShowProgressionTitleTooltip(string action)
        => MaterialText.SetTooltip(UiText.T(action) + "\n" + UiText.T("Chocobo progression") + "\n" + UiText.T(
            Plugin.PlayerState.ContentId == 0 ? "Log into a character to see chocobo progression." : plugin.BreedingService.StatusText));

    private void DrawProgressionOverview(ChocoboTrainingSnapshot snapshot)
    {
        var contentId = Plugin.PlayerState.ContentId;
        var now = DateTime.UtcNow;
        if (contentId == 0)
        {
            UiGui.TextDisabled("Log into a character to see chocobo progression.");
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
        var scale = MaterialTheme.Metrics.Scale;
        using (var panel = new ChokePanel("##ProgressionPresentation"))
        {
            if (panel.Visible)
            {
                var origin = ImGui.GetCursorScreenPos();
                var headerWidth = ImGui.GetContentRegionAvail().X;
                var settingsWidth = UiGui.ButtonWidth("Open VERMAXION settings", MaterialIcon.ExternalLink);
                var inlineSettings = headerWidth >= settingsWidth + 440 * scale;
                ChokePresentation.Brand(origin, 48 * scale);
                ImGui.SetCursorScreenPos(origin + new Vector2(58 * scale, 0));
                using (UiText.Font(ChokePresentation.Compact ? UiFontRole.CompactPaneHeading : UiFontRole.PaneHeading)) UiGui.Text("Chocobo progression");
                ImGui.SetCursorScreenPos(origin + new Vector2(58, 32) * scale);
                ImGui.PushTextWrapPos(inlineSettings ? ImGui.GetCursorPosX() + headerWidth - 58 * scale - settingsWidth - 15 * scale : 0);
                MaterialText.TextColored(MaterialTheme.Current.Colors.OnSurfaceVariant, UiText.T("VERMAXION owns progression settings and the daily racing allowance. Choke-abo carries out feeding and breeding."));
                ImGui.PopTextWrapPos();
                var headerBottom = ImGui.GetCursorScreenPos().Y;
                if (inlineSettings) ImGui.SetCursorScreenPos(origin + new Vector2(headerWidth - settingsWidth, 10 * scale));
                else ImGui.Spacing();
                if (UiGui.Button("Open VERMAXION settings", icon: MaterialIcon.ExternalLink))
                    Plugin.CommandManager.ProcessCommand("/vmx chocobo settings");
                if (inlineSettings) ImGui.SetCursorScreenPos(new Vector2(origin.X, Math.Max(headerBottom, ImGui.GetItemRectMax().Y + ImGui.GetStyle().ItemSpacing.Y)));
                ImGui.Spacing();
                var actionWidth = Math.Max(90 * scale, MathF.Floor((ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3));
                var wide = ImGui.GetContentRegionAvail().X >= 3 * Math.Max(UiGui.ButtonWidth("Resume", MaterialIcon.Play), Math.Max(UiGui.ButtonWidth("Pause", MaterialIcon.Pause), UiGui.ButtonWidth("Stop", MaterialIcon.Stop))) + 2 * ImGui.GetStyle().ItemSpacing.X;
                using (var action = new MaterialStyleScope())
                {
                    action.Color(ImGuiCol.Button, MaterialTheme.Current.Colors.Primary); action.Color(ImGuiCol.Text, MaterialTheme.Current.Colors.OnPrimary);
                    if (UiGui.Button("Resume##Progression", new Vector2(wide ? actionWidth : -1, 0), MaterialIcon.Play))
                        RunProgressionCommandFromUi("resume");
                }
                if (wide) ImGui.SameLine();
                if (UiGui.Button("Pause##Progression", new Vector2(wide ? actionWidth : -1, 0), MaterialIcon.Pause))
                    RunProgressionCommandFromUi("pause");
                if (wide) ImGui.SameLine();
                if (UiGui.Button("Stop##Progression", new Vector2(wide ? actionWidth : -1, 0), MaterialIcon.Stop))
                    StopProgressionFromUi();
                ImGui.Spacing();
                var originalRoot = ImGui.GetID("");
                using (var summary = new ChokePanel("##ProgressionSummaryPresentation", padding: ChokePresentation.Compact ? 8 : 12))
                {
                    using var summaryStyle = new MaterialStyleScope();
                    summaryStyle.Style(ImGuiStyleVar.CellPadding, new Vector2(ChokePresentation.Compact ? 8 : 10, ChokePresentation.Compact ? 5 : 7) * scale);
                    if (summary.Visible && ImGui.BeginTable("##ProgressionSummary", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV))
                    {
                        ImGui.TableSetupColumn("##left-label", ImGuiTableColumnFlags.WidthStretch, .19f);
                        ImGui.TableSetupColumn("##left-value", ImGuiTableColumnFlags.WidthStretch, .31f);
                        ImGui.TableSetupColumn("##right-label", ImGuiTableColumnFlags.WidthStretch, .22f);
                        ImGui.TableSetupColumn("##right-value", ImGuiTableColumnFlags.WidthStretch, .28f);
                        ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); UiGui.Text("Pedigree");
                        ImGui.TableSetColumnIndex(1); MaterialText.Text(status.Pedigree > 0 ? status.Pedigree.ToString(UiText.Current.Culture) : UiText.T("Unknown"));
                        ImGui.TableSetColumnIndex(2); UiGui.Text("Inherited ability");
                        ImGui.TableSetColumnIndex(3); DrawAbilityValue(snapshot.InheritedAbilityId);
                        ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); UiGui.Text("Rank");
                        ImGui.TableSetColumnIndex(1); MaterialText.Text(status.RacingRank > 0 ? status.RacingRank.ToString(UiText.Current.Culture) : UiText.T("Unknown"));
                        ImGui.TableSetColumnIndex(2); UiGui.Text("Learned ability");
                        ImGui.TableSetColumnIndex(3); DrawAbilityValue(snapshot.LearnedAbilityId);
                        ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); UiGui.Text("Goal");
                        ImGui.TableSetColumnIndex(1); UiGui.Text(state.ExecutionOwner != BreedingExecutionOwner.Target || state.TargetPedigree < 2 ? "Not configured"
                            : state.OffspringGoal == OffspringGoal.ReachPedigree ? UiText.F("Pedigree G{0}", state.TargetPedigree)
                            : state.OffspringGoal == OffspringGoal.AbilityOffspring ? "Offspring printer" : "Colour seeker");
                        ImGui.TableSetColumnIndex(2); UiGui.Text("Colour");
                        ImGui.TableSetColumnIndex(3);
                        if (Plugin.DataManager.GetExcelSheet<Stain>().TryGetRow(snapshot.ColourId, out var colour))
                        {
                            ImGuiP.PushOverrideID(originalRoot); DrawColourSwatch(colour.Color); ImGui.PopID();
                            ImGui.SameLine(); MaterialText.TextWrapped(colour.Name.ExtractText());
                        }
                        else UiGui.Text("Unknown");
                        ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); UiGui.Text("Phase");
                        ImGui.TableSetColumnIndex(1); UiGui.Text(DescribePhase(state, status));
                        ImGui.TableSetColumnIndex(2); UiGui.Text("Covering ready in"); ImGui.TableSetColumnIndex(3);
                        var coveringAt = state.CoveringEligibleAtUtc;
                        if ((state.Phase == BreedingPhase.CoveringWait || state.Phase == BreedingPhase.Paused && state.PhaseBeforePause == BreedingPhase.CoveringWait) && coveringAt != DateTime.MinValue)
                        {
                            var remaining = coveringAt.ToUniversalTime() - now;
                            MaterialText.Text(remaining > TimeSpan.Zero ? remaining.ToString(@"hh\:mm\:ss", UiText.Current.Culture) : UiText.T("Ready"));
                            if (ImGui.IsItemHovered()) UiGui.SetTooltip(coveringAt.ToLocalTime().ToString("g", UiText.Current.Culture));
                        }
                        else UiGui.Text("Unknown");
                        ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0); UiGui.Text("Next action");
                        ImGui.TableSetColumnIndex(1); UiGui.TextWrapped(UiText.T(status.Reason));
                        ImGui.EndTable();
                    }
                }
                if (state.ExecutionOwner == BreedingExecutionOwner.Target && state.TargetPedigree >= 2)
                {
                    if (state.OffspringGoal != OffspringGoal.ReachPedigree)
                    {
                        UiGui.Text(UiText.F("Matching G9 offspring: {0:N0} / {1:N0}", status.MatchingOffspringProduced, status.MatchingOffspringRequested));
                        ImGui.ProgressBar(status.MatchingOffspringRequested > 0 ? Math.Clamp((float)status.MatchingOffspringProduced / status.MatchingOffspringRequested, 0, 1) : 0,
                            new Vector2(-1, 0), UiText.T("Matching offspring retained unregistered"));
                        if (state.OffspringGoal == OffspringGoal.AbilityOffspring) DrawAbility("Desired inherited ability", state.DesiredInheritedAbilityId);
                        else foreach (var colourId in state.AcceptableColourIds)
                            if (Plugin.DataManager.GetExcelSheet<Stain>().TryGetRow(colourId, out var acceptableColour))
                            { DrawColourSwatch(acceptableColour.Color); ImGui.SameLine(); MaterialText.Text(acceptableColour.Name.ExtractText()); }
                    }
                    UiGui.Text(UiText.F("Breeding mode: {0}", UiText.T(state.BreedingMode == BreedingMode.OwnedParents ? "Owned parents" : "NPC covering permits")));
                    if (state.BreedingMode == BreedingMode.NpcPermits) UiGui.TextDisabled(state.OffspringGoal != OffspringGoal.ReachPedigree ? "Permit objective: produce G9 offspring"
                        : state.ProduceCounterpart ? "Permit objective: produce a missing current-pedigree counterpart" : "Permit objective: advance pedigree");
                    UiGui.Text(UiText.F("Reserves: {0:N0} gil    {1:N0} MGP", state.GilReserve, state.MgpReserve));
                    var feedPolicy = state.FeedPolicy switch { InsufficientFeedPolicy.FallBack => "Fall back to Grade 1, then skip", InsufficientFeedPolicy.Stop => "Stop until Resume", _ => "Skip this feeding round" };
                    UiGui.Text(UiText.F("Feed: Grade {0}    If unavailable: {1}", state.PreferredFeedGrade, UiText.T(feedPolicy)));
                }
                else UiGui.TextWrapped("Choose your progression goal, reserves and feeding policy in VERMAXION settings, then Resume to begin.");
                if (status.RacingRank == 0) UiGui.TextDisabled(snapshot.IsLoaded ? "No registered racing chocobo." : "Registered racer data is unavailable.");
            }
        }
        ImGui.Spacing();
        float CellWidth(string heading, IEnumerable<string> values) => MathF.Ceiling(values.Prepend(UiText.T(heading)).Max(value => MaterialText.Measure(value).X));
        var pedigreeWidth = CellWidth("Pedigree", breedingStock.Select(form => form.Pedigree > 0 ? form.Pedigree.ToString(UiText.Current.Culture) : UiText.T("Covering proof")));
        var sexWidth = CellWidth("Sex", breedingStock.Select(form => UiText.T(form.Sex.ToString())));
        var quantityWidth = CellWidth("Quantity", breedingStock.Select(form => form.Quantity.ToString(UiText.Current.Culture)));
        var coveringsWidth = CellWidth("Coverings left", breedingStock.Select(form => form.Kind == ChocoboFormKind.Retired ? form.Capacity.ToString(UiText.Current.Culture) : "-"));
        var minimumStockWidth = Math.Max(160 * scale, MaterialText.Measure(UiText.T("Item")).X) + pedigreeWidth + sexWidth + quantityWidth + coveringsWidth
            + 10 * (ChokePresentation.Compact ? 6 : 10) * scale + 6 * scale;
        var stockPanelWidth = Math.Max(ImGui.GetContentRegionAvail().X, minimumStockWidth + 2 * (ChokePresentation.Compact ? 12 : 16) * scale);
        using (var stock = new ChokePanel("##StockPresentation", stockPanelWidth))
        {
            if (!stock.Visible) return;
            MaterialIcons.Draw(MaterialIcon.Database, ImGui.GetCursorScreenPos(), 30 * scale, MaterialTheme.Current.Colors.Primary);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 42 * scale);
            using (UiText.Font(ChokePresentation.Compact ? UiFontRole.CompactPaneHeading : UiFontRole.PaneHeading)) UiGui.Text("Breeding stock");
            UiGui.TextDisabled("Offspring sex is random. Two coverings do not guarantee a male and a female.");
            using var stockTableStyle = new MaterialStyleScope();
            stockTableStyle.Style(ImGuiStyleVar.CellPadding, new Vector2(ChokePresentation.Compact ? 6 : 10, ChokePresentation.Compact ? 4 : 8) * scale);
            using var tightRows = ChokePresentation.Compact ? MaterialTable.PushTightRows() : default;
            var padding = 2 * ImGui.GetStyle().CellPadding.X;
            var stockWidth = Math.Max(ImGui.GetContentRegionAvail().X, minimumStockWidth);
            var normalColumn = stockWidth / 7.5f;
            if (ImGui.BeginTable("BreedingStock", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders, new Vector2(stockWidth, 0)))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3, 0);
                ImGui.TableSetupColumn("Pedigree / sex", ImGuiTableColumnFlags.WidthFixed, Math.Max(pedigreeWidth, normalColumn - padding), 1);
                ImGui.TableSetupColumn("Sex", ImGuiTableColumnFlags.WidthFixed, Math.Max(sexWidth, normalColumn - padding), 4);
                ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, Math.Max(quantityWidth, normalColumn - padding), 2);
                ImGui.TableSetupColumn("Coverings left", ImGuiTableColumnFlags.WidthFixed, Math.Max(coveringsWidth, normalColumn * 1.5f - padding), 3);
                UiGui.TableHeadersRow(static name => name == "Pedigree / sex" ? "Pedigree" : name);
                foreach (var form in breedingStock)
                {
                    ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                    if (Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(form.ItemId, out var item)) { DrawGameIcon(item.Icon, ChokePresentation.Compact ? 32 : 36); ImGui.SameLine(); }
                    MaterialText.TextWrapped(form.ItemName);
                    if (ChocoboInventoryModel.TryReadInheritedAbility(form, out var stockAbilityId))
                    { var stockAbility = Plugin.DataManager.GetExcelSheet<ChocoboRaceAbility>().GetRow(stockAbilityId); DrawGameIcon(stockAbility.Icon); ImGui.SameLine(); MaterialText.Text(stockAbility.Name.ExtractText()); }
                    if (ChocoboInventoryModel.TryReadColour(form, out var colourId))
                    { var colour = Plugin.DataManager.GetExcelSheet<Stain>().GetRow(colourId); ImGui.PushID($"StockColour{form.Container}{form.Slot}"); DrawColourSwatch(colour.Color); ImGui.PopID(); ImGui.SameLine(); MaterialText.Text(colour.Name.ExtractText()); }
                    ImGui.TableSetColumnIndex(1); MaterialText.Text(form.Pedigree > 0 ? form.Pedigree.ToString(UiText.Current.Culture) : UiText.T("Covering proof"));
                    ImGui.TableSetColumnIndex(2); UiGui.Text(form.Sex.ToString());
                    ImGui.TableSetColumnIndex(3); MaterialText.Text(form.Quantity.ToString(UiText.Current.Culture));
                    ImGui.TableSetColumnIndex(4); MaterialText.Text(form.Kind == ChocoboFormKind.Retired ? form.Capacity.ToString(UiText.Current.Culture) : "-");
                }
                ImGui.EndTable();
            }
            if (breedingStock.Count == 0) UiGui.TextDisabled("No breeding forms or covering proof in inventory.");
            DrawStockCleanup();
        }
    }

    private void DrawStockCleanup()
    {
        var service = plugin.BreedingService;
        ImGui.BeginDisabled(service.StockCleanupRunning);
        if (UiGui.Button("Clean up G1-G8 stock"))
        {
            cleanupFledglings = cleanupRetired = true;
            cleanupPermissions = false;
            RefreshStockCleanupPreview();
            ImGui.OpenPopup("##StockCleanup");
        }
        ImGui.EndDisabled();
        if (service.StockCleanupTotal > 0)
            MaterialText.Text(UiText.F("Stock cleanup: {0:N0} / {1:N0} removed.", service.StockCleanupRemoved, service.StockCleanupTotal));
        if (service.StockCleanupStatus != "Idle") UiGui.TextWrapped(service.StockCleanupStatus);
        if (service.StockCleanupRunning && UiGui.Button("Cancel cleanup")) service.CancelStockCleanup();

        var scale = MaterialTheme.Metrics.Scale;
        ImGui.SetNextWindowSize(new Vector2(600 * scale, 0));
        if (!ImGui.BeginPopup("##StockCleanup")) return;
        try
        {
            UiGui.Text("Clean up G1-G8 stock");
            UiGui.TextWrapped("Discard is permanent. G9 stock, covering proof and selected breeding inputs are protected.");
            var changed = UiGui.Toggle("Fledglings", ref cleanupFledglings);
            changed |= UiGui.Toggle("Retired registrations", ref cleanupRetired);
            changed |= UiGui.Toggle("Purchased covering permissions", ref cleanupPermissions);
            if (changed || UiGui.Button("Preview cleanup")) RefreshStockCleanupPreview();
            if (stockCleanupPreview is { } preview)
            {
                if (ImGui.BeginChild("##CleanupStockRows", new Vector2(0, Math.Min(300 * scale,
                    Math.Max(60 * scale, preview.Items.Count * ImGui.GetFrameHeightWithSpacing()))), true))
                {
                    for (var index = 0; index < preview.Items.Count; index++)
                    {
                        var item = preview.Items[index];
                        ImGui.PushID(index);
                        try
                        {
                            ImGui.Checkbox("##selected", ref stockCleanupSelected[index]);
                            ImGui.SameLine();
                            MaterialText.TextWrapped(item.ItemName + "  × " + item.Quantity.ToString(UiText.Current.Culture));
                        }
                        finally { ImGui.PopID(); }
                    }
                }
                ImGui.EndChild();
                var selected = preview.Items.Where((_, index) => stockCleanupSelected[index]).ToArray();
                ImGui.BeginDisabled(selected.Length == 0);
                if (UiGui.Button("Discard selected stock") && service.StartStockCleanup(preview, selected))
                {
                    stockCleanupPreview = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndDisabled();
            }
            UiGui.TextWrapped(service.StockCleanupStatus);
            if (UiGui.Button("Cancel")) { stockCleanupPreview = null; ImGui.CloseCurrentPopup(); }
        }
        finally { ImGui.EndPopup(); }
    }

    private void RefreshStockCleanupPreview()
    {
        stockCleanupPreview = plugin.BreedingService.PreviewStockCleanup(cleanupFledglings, cleanupRetired, cleanupPermissions);
        stockCleanupSelected = stockCleanupPreview?.Items.Select(_ => true).ToArray() ?? [];
    }

    private static string DescribePhase(BreedingCharacterState state, TargetCycleStatus status) => state.Phase switch
    {
        BreedingPhase.CoveringWait => "Covering in progress",
        BreedingPhase.AdoptionPendingCapture => "Collecting offspring",
        BreedingPhase.RegistrationPendingCapture => "Registering offspring",
        BreedingPhase.RetirementPendingCapture => state.OffspringGoal == OffspringGoal.ReachPedigree ? "Retiring the intermediate racer" : "Retiring the G9 breeding parent",
        BreedingPhase.CoveringPendingCapture => "Preparing covering",
        BreedingPhase.Paused => "Paused - Resume is required",
        BreedingPhase.Blocked => "Needs attention",
        BreedingPhase.TargetReady => status.ProductionComplete ? "Offspring goal reached" : status.ProgressionComplete ? "Progression complete" : "Verifying the registered racer",
        BreedingPhase.Racing => "Ready for racing", BreedingPhase.Feeding => "Feeding the racer",
        BreedingPhase.PurchasingFeed or BreedingPhase.PurchasingSupplies => "Purchasing supplies",
        _ => "Ready to plan the next action",
    };

    private static void DrawAbilityValue(uint abilityId)
    {
        if (abilityId != 0 && Plugin.DataManager.GetExcelSheet<ChocoboRaceAbility>().TryGetRow(abilityId, out var ability))
        { DrawGameIcon(ability.Icon); ImGui.SameLine(); MaterialText.TextWrapped(ability.Name.ExtractText()); }
        else UiGui.Text("Unknown");
    }

    private static void DrawAbility(string label, uint abilityId)
    {
        if (abilityId != 0 && Plugin.DataManager.GetExcelSheet<ChocoboRaceAbility>().TryGetRow(abilityId, out var ability))
        {
            DrawGameIcon(ability.Icon);
            ImGui.SameLine();
            UiGui.TextUnformatted(UiText.Interpolated($"{UiText.T(label)}: {ability.Name.ExtractText()}"));
        }
        else
            UiGui.TextDisabled(UiText.Interpolated($"{UiText.T(label)}: none"));
    }

    private static void DrawGameIcon(uint iconId, float size = 24)
    {
        var texture = Plugin.TextureProvider.GetFromGameIcon(iconId).GetWrapOrDefault();
        if (texture != null)
            ImGui.Image(texture.Handle, new Vector2(size) * MaterialTheme.Metrics.Scale);
    }

    private static void DrawColourSwatch(uint rgb)
        => ImGui.ColorButton("##RacerColour", new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1),
            ImGuiColorEditFlags.NoTooltip, new Vector2(ChokePresentation.Compact ? 28 : 32) * MaterialTheme.Metrics.Scale);

    private void DrawCompactWorkspace(Configuration cfg, ChocoboTrainingSnapshot snapshot, ChocoboTrainingSnapshot projected, FeedPurchasePlan purchasePlan, int plannedSessions)
    {
        if (ImGui.GetContentRegionAvail().X < Math.Max(900 * MaterialTheme.Metrics.Scale, PlanMinimumWidth() / .48f))
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
        UiGui.TextUnformatted("Overview");
        if (!snapshot.IsLoaded)
        {
            UiGui.TextDisabled("No chocobo data loaded yet.");
            return;
        }

        if (ImGui.BeginTable("ChokeAboOverviewSummary", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            UiGui.Text(UiText.Interpolated($"Racing rank: {snapshot.Rank}/50"));
            UiGui.Text(UiText.Interpolated($"Rating: {snapshot.Rating}"));
            UiGui.Text(UiText.Interpolated($"Pedigree: G{snapshot.PedigreeLevel}"));
            UiGui.Text(UiText.Interpolated($"XP: {snapshot.ExperienceCurrent:N0}/{snapshot.ExperienceMax:N0}"));
            UiGui.Text(UiText.Interpolated($"Sessions: {snapshot.SessionsAvailable}"));
            UiGui.Text(UiText.Interpolated($"Plan: {plannedSessions}"));

            ImGui.TableSetColumnIndex(1);
            UiGui.Text(UiText.Interpolated($"Gil: {purchasePlan.CurrentGil:N0}"));
            UiGui.Text(UiText.Interpolated($"MGP: {purchasePlan.CurrentMgp:N0}"));
            ImGui.EndTable();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Source))
            UiGui.TextDisabled(snapshot.Source);

        var gilColor = purchasePlan.TotalGil == 0 || purchasePlan.CanAffordGil
            ? new Vector4(0.42f, 1.0f, 0.56f, 1.0f)
            : new Vector4(1.0f, 0.58f, 0.58f, 1.0f);
        var mgpColor = purchasePlan.TotalMgp == 0 || purchasePlan.CanAffordMgp
            ? new Vector4(0.42f, 1.0f, 0.56f, 1.0f)
            : new Vector4(1.0f, 0.58f, 0.58f, 1.0f);
        UiGui.TextColored(gilColor, UiText.Interpolated($"Need gil: {purchasePlan.TotalGil:N0}"));
        UiGui.SameLineIfFits(MaterialText.Measure(UiText.F("Need MGP: {0:N0}", purchasePlan.TotalMgp)).X);
        UiGui.TextColored(mgpColor, UiText.Interpolated($"Need MGP: {purchasePlan.TotalMgp:N0}"));

        var starsWidth = MathF.Ceiling(Math.Max(56 * MaterialTheme.Metrics.Scale, MaterialText.Measure(UiText.T("Stars")).X));
        var nowWidth = MathF.Ceiling(Math.Max(88 * MaterialTheme.Metrics.Scale, MaterialText.Measure(UiText.T("Now")).X));
        var afterWidth = MathF.Ceiling(Math.Max(88 * MaterialTheme.Metrics.Scale, MaterialText.Measure(UiText.T("After")).X));
        var projectionWidth = Math.Max(ImGui.GetContentRegionAvail().X, MaterialText.Measure(UiText.T("Stat")).X + starsWidth + nowWidth + afterWidth + 8 * ImGui.GetStyle().CellPadding.X + 5 * MaterialTheme.Metrics.Scale);
        if (ImGui.BeginTable("ChokeAboCurrentAndProjected", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV, new Vector2(projectionWidth, 0)))
        {
            ImGui.TableSetupColumn("Stat");
            ImGui.TableSetupColumn("Stars", ImGuiTableColumnFlags.WidthFixed, starsWidth);
            ImGui.TableSetupColumn("Now", ImGuiTableColumnFlags.WidthFixed, nowWidth);
            ImGui.TableSetupColumn("After", ImGuiTableColumnFlags.WidthFixed, afterWidth);
            UiGui.TableHeadersRow();

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
        using var tightRows = plugin.Configuration.UiCompact ? MaterialTable.PushTightRows() : default;
        UiGui.TextUnformatted("Training Plan");
        UiGui.TextWrapped("Choose the number of training sessions and feed grade for each stat.");

        var changed = false;
        var cellPadding = 2 * ImGui.GetStyle().CellPadding.X;
        var quantityWidth = MathF.Ceiling(Math.Max(80 * MaterialTheme.Metrics.Scale, MaterialText.Measure("-00000").X + 2 * ImGui.GetStyle().FramePadding.X)) + cellPadding;
        float GradeWidth(string label) => Math.Max(60 * MaterialTheme.Metrics.Scale, MaterialText.Measure(UiText.T(label)).X + cellPadding);
        if (ImGui.BeginTable("ChokeAboPlanTable", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV, new Vector2(Math.Max(ImGui.GetContentRegionAvail().X, PlanMinimumWidth()), 0)))
        {
            ImGui.TableSetupColumn("Stat", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, quantityWidth);
            ImGui.TableSetupColumn("Grade 1", ImGuiTableColumnFlags.WidthFixed, GradeWidth("Grade 1"));
            ImGui.TableSetupColumn("Grade 2", ImGuiTableColumnFlags.WidthFixed, GradeWidth("Grade 2"));
            ImGui.TableSetupColumn("Grade 3", ImGuiTableColumnFlags.WidthFixed, GradeWidth("Grade 3"));
            UiGui.TableHeadersRow();

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
        if (UiGui.Button("Clear Plan"))
        {
            plugin.ClearPlan();
            return;
        }

        if (changed)
            cfg.Save();

        var plannedSessions = ChocoboStatsService.GetPlannedTrainingCount(cfg);
        UiGui.Text(UiText.Interpolated($"Planned trainings this cycle: {plannedSessions}"));
        if (snapshot.SessionsAvailable > 0)
            UiGui.Text(UiText.Interpolated($"Session cap: {snapshot.SessionsAvailable}"));
    }

    private void DrawPurchasePlan(ChocoboTrainingSnapshot snapshot, FeedPurchasePlan purchasePlan)
    {
        using var tightRows = ChokePresentation.Compact ? MaterialTable.PushTightRows() : default;
        UiGui.TextUnformatted("Purchase Plan");
        UiGui.TextWrapped("Exact shopping list for the current plan.");

        if (!purchasePlan.IsLoaded)
        {
            UiGui.TextDisabled("Load racing chocobo data first so the purchase plan can compare against sessions available.");
        }
        else if (purchasePlan.PlannedTrainings > purchasePlan.SessionsAvailable)
        {
            UiGui.TextColored(
                new Vector4(1.0f, 0.70f, 0.35f, 1.0f),
                UiText.Interpolated($"Plan exceeds sessions available: {purchasePlan.PlannedTrainings}/{purchasePlan.SessionsAvailable}. The shopping list below still reflects the full plan."));
        }

        float PurchaseWidth(string heading, float minimum, IEnumerable<string> values) => MathF.Ceiling(Math.Max(minimum * MaterialTheme.Metrics.Scale,
            values.Prepend(UiText.T(heading)).Max(value => MaterialText.Measure(value).X)));
        var plannedWidth = PurchaseWidth("Plan", 44, purchasePlan.Entries.Select(entry => entry.PlannedQuantity.ToString(UiText.Current.Culture)));
        var onHandWidth = PurchaseWidth("Qty on hand", 88, purchasePlan.Entries.Select(entry => entry.OnHandQuantity.ToString(UiText.Current.Culture)));
        var currencyWidth = PurchaseWidth("Currency", 72, purchasePlan.Entries.Select(entry => UiText.T(entry.CurrencyKind == FeedCurrencyKind.Gil ? "Gil" : "MGP")));
        var totalWidth = PurchaseWidth("Total", 86, purchasePlan.Entries.Select(entry => entry.TotalCost.ToString("N0", UiText.Current.Culture)));
        var tableWidth = Math.Max(ImGui.GetContentRegionAvail().X, Math.Max(160 * MaterialTheme.Metrics.Scale, MaterialText.Measure(UiText.T("Feed")).X)
            + plannedWidth + onHandWidth + currencyWidth + totalWidth + 10 * ImGui.GetStyle().CellPadding.X + 6 * MaterialTheme.Metrics.Scale);
        if (ImGui.BeginTable("ChokeAboPurchasePlanTable", 5, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV, new Vector2(tableWidth, 0)))
        {
            ImGui.TableSetupColumn("Feed");
            ImGui.TableSetupColumn("Plan", ImGuiTableColumnFlags.WidthFixed, plannedWidth);
            ImGui.TableSetupColumn("Qty on hand", ImGuiTableColumnFlags.WidthFixed, onHandWidth);
            ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthFixed, currencyWidth);
            ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, totalWidth);
            UiGui.TableHeadersRow();

            if (purchasePlan.Entries.Count == 0)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                UiGui.TextDisabled("No planned feed purchases.");
            }
            else
            {
                foreach (var entry in purchasePlan.Entries)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    MaterialText.TextWrapped(entry.FeedName);
                    ImGui.TableSetColumnIndex(1);
                    MaterialText.Text(entry.PlannedQuantity.ToString(UiText.Current.Culture));
                    ImGui.TableSetColumnIndex(2);
                    MaterialText.Text(entry.OnHandQuantity.ToString(UiText.Current.Culture));
                    ImGui.TableSetColumnIndex(3);
                    UiGui.TextUnformatted(entry.CurrencyKind == FeedCurrencyKind.Gil ? "Gil" : "MGP");
                    ImGui.TableSetColumnIndex(4);
                    UiGui.TextUnformatted(UiText.Interpolated($"{entry.TotalCost:N0}"));
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

        UiGui.TextColored(gilColor, UiText.Interpolated($"Total gil needed: {purchasePlan.TotalGil:N0}"));
        UiGui.SameLineIfFits(MaterialText.Measure(UiText.F("Total MGP needed: {0:N0}", purchasePlan.TotalMgp)).X);
        UiGui.TextColored(mgpColor, UiText.Interpolated($"Total MGP needed: {purchasePlan.TotalMgp:N0}"));

        if (!purchasePlan.CanAffordGil || !purchasePlan.CanAffordMgp)
        {
            UiGui.TextColored(
                new Vector4(1.0f, 0.70f, 0.35f, 1.0f),
                "Current currency is below the planned purchase total. Adjust the plan or stock up before the buy loop is enabled.");
        }

        ImGui.Spacing();
        if (UiGui.Button("Buy Needed Feed"))
            plugin.StartBuyOnly();

        UiGui.SameLineIfFits(UiGui.ButtonWidth("Feed Planned Sessions"));
        if (UiGui.Button("Feed Planned Sessions"))
            plugin.StartFeedOnly();

        UiGui.SameLineIfFits(UiGui.ButtonWidth("Run Full Cycle"));
        if (UiGui.Button("Run Full Cycle"))
            plugin.StartFullCycle();

        UiGui.SameLineIfFits(UiGui.ButtonWidth("Start / Resume Breeding"));
        if (UiGui.Button("Start / Resume Breeding"))
            plugin.StartBreeding();

        UiGui.SameLineIfFits(UiGui.ButtonWidth("Stop"));
        if (UiGui.Button("Stop"))
            plugin.StopAutomation();

        UiGui.TextDisabled("Automation uses the session-capped plan and subtracts feed already on hand before buying.");
        UiGui.Text(UiText.Interpolated($"Cleanup status: {UiText.T(plugin.CleanupStatusText)}"));
        UiGui.Text(UiText.Interpolated($"Buy status: {UiText.T(plugin.VendorPurchaseService.StatusText)}"));
        UiGui.Text(UiText.Interpolated($"Feed status: {UiText.T(plugin.StableFeedingService.StatusText)}"));
        UiGui.Text(UiText.Interpolated($"Breeding status: {UiText.T(plugin.BreedingService.StatusText)}"));
    }

    private static void DrawStatRow(string label, ChocoboStatSnapshot stat)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        UiGui.TextWrapped(label);
        ImGui.TableSetColumnIndex(1);
        MaterialText.Text(FormatStat(stat.Current, UiText.Current.Culture));
        ImGui.TableSetColumnIndex(2);
        MaterialText.Text(FormatStat(stat.Maximum, UiText.Current.Culture));
    }

    private static void DrawProjectionRow(string label, ChocoboStatSnapshot current, ChocoboStatSnapshot projected)
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        UiGui.TextWrapped(label);
        ImGui.TableSetColumnIndex(1);
        UiGui.Text(current.Stars == 1 ? "1 star" : UiText.F("{0} stars", current.Stars));
        ImGui.TableSetColumnIndex(2);
        MaterialText.Text(FormatStatPair(current, UiText.Current.Culture));
        ImGui.TableSetColumnIndex(3);

        if (projected.Current > current.Current)
        {
            UiGui.TextColored(new Vector4(0.42f, 1.0f, 0.56f, 1.0f), FormatStatPair(projected, UiText.Current.Culture));
        }
        else
        {
            MaterialText.Text(FormatStatPair(projected, UiText.Current.Culture));
        }
    }

    private static bool DrawPlanInput(string label, ref int value)
    {
        var local = value;
        if (!UiGui.InputInt(label, ref local))
            return false;

        value = Math.Max(0, local);
        return true;
    }

    private static float PlanMinimumWidth()
    {
        var scale = MaterialTheme.Metrics.Scale;
        var padding = 2 * ImGui.GetStyle().CellPadding.X;
        var stat = new[] { "Maximum Speed", "Acceleration", "Endurance", "Stamina", "Cunning" }.Max(label => MaterialText.Measure(UiText.T(label)).X) + padding;
        var quantity = MathF.Ceiling(Math.Max(80 * scale, MaterialText.Measure("-00000").X + 2 * ImGui.GetStyle().FramePadding.X)) + padding;
        var grades = new[] { "Grade 1", "Grade 2", "Grade 3" }.Sum(label => Math.Max(60 * scale, MaterialText.Measure(UiText.T(label)).X + padding));
        return stat + quantity + grades + 8 * scale;
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
        UiGui.TextWrapped(label);

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

    private static string FormatStatPair(ChocoboStatSnapshot stat, CultureInfo? culture = null)
        => $"{FormatStat(stat.Current, culture)}/{FormatStat(stat.Maximum, culture)}";

    private static string FormatStars(ChocoboStatSnapshot stat)
        => stat.Stars == 1 ? "1 star" : $"{stat.Stars} stars";

    private static string FormatStat(decimal value, CultureInfo? culture = null)
        => value.ToString("0.#", culture ?? CultureInfo.InvariantCulture);
}
