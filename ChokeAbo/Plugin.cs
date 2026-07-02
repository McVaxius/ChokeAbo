using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ChokeAbo.Services;
using ChokeAbo.Windows;
using System.Linq;

namespace ChokeAbo;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string DutyMainWindowDeniedMessage = "Choke-abo main window closed because you are in a duty.";
    private static readonly TimeSpan DutyToastThrottle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CleanupRetryDelay = TimeSpan.FromSeconds(2);

    public Configuration Configuration { get; }
    internal ChocoboStatsService ChocoboStatsService { get; }
    internal InventoryService InventoryService { get; }
    internal VendorPurchaseService VendorPurchaseService { get; }
    internal StableFeedingService StableFeedingService { get; }
    public WindowSystem WindowSystem { get; } = new(PluginInfo.InternalName);
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private IDtrBarEntry? dtrEntry;
    private DateTime lastDutyDeniedToastUtc = DateTime.MinValue;
    private CleanupMode cleanupMode;
    private CleanupPhase cleanupPhase;
    private DateTime cleanupRetryAtUtc = DateTime.MinValue;
    private int cleanupPassNumber;
    private int remainingSessionBudget;
    private string cleanupRetryReason = string.Empty;
    private string cleanupTerminalStatus = "Idle";

    private enum CleanupMode
    {
        None,
        FeedOnly,
        FullCycle,
    }

    private enum CleanupPhase
    {
        Idle,
        Purchasing,
        Feeding,
        WaitingToRetry,
    }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        InventoryService = new GameInventoryService(DataManager, Log);
        ChocoboStatsService = new ChocoboStatsService(CommandManager, GameGui, Log, InventoryService);
        VendorPurchaseService = new VendorPurchaseService(InventoryService, Log);
        StableFeedingService = new StableFeedingService(InventoryService, GameGui, Log, Configuration);
        mainWindow = new MainWindow(this, ChocoboStatsService);
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        CommandManager.AddHandler(PluginInfo.Command, new CommandInfo(OnCommand) { HelpMessage = $"Open {PluginInfo.DisplayName}. Use {PluginInfo.Command} refresh to refresh racing chocobo data or {PluginInfo.Command} config for settings." });
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;
        SetupDtrBar();
        UpdateDtrBar();
        Log.Information("[Choke-abo] Fixed-build marker 2026-07-01: delayed GoldSaucerInfo Chocobo callbacks use payload 130 with payload 131 fallback and 2.0s ready gating.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        CommandManager.RemoveHandler(PluginInfo.Command);
        WindowSystem.RemoveAllWindows();
        dtrEntry?.Remove();
    }

    public void ToggleMainUi()
    {
        if (DenyMainWindowInDuty(forceToast: true))
            return;

        mainWindow.Toggle();
    }

    public void ToggleConfigUi() => configWindow.Toggle();
    public void PrintStatus(string m) => ChatGui.Print($"[{PluginInfo.DisplayName}] {m}");
    public bool IsAutomationRunning => VendorPurchaseService.IsRunning || StableFeedingService.IsRunning || cleanupMode != CleanupMode.None;
    public string CleanupStatusText => cleanupPhase switch
    {
        CleanupPhase.Purchasing => $"Pass {cleanupPassNumber}: buying missing feed ({remainingSessionBudget} sessions remain).",
        CleanupPhase.Feeding => $"Pass {cleanupPassNumber}: feeding ({remainingSessionBudget} sessions remain).",
        CleanupPhase.WaitingToRetry => $"Pass {cleanupPassNumber}: cleanup/retry in {GetRetrySecondsRemaining():0.0}s. {cleanupRetryReason}",
        _ => cleanupTerminalStatus,
    };

    private bool IsInDuty()
        => Condition[ConditionFlag.BoundByDuty] || Condition[ConditionFlag.BoundByDuty56];

    private bool DenyMainWindowInDuty(bool forceToast = false)
    {
        if (!IsInDuty())
            return false;

        mainWindow.IsOpen = false;
        ShowDutyDeniedToast(forceToast);
        return true;
    }

    private void ShowDutyDeniedToast(bool forceToast)
    {
        var now = DateTime.UtcNow;
        if (!forceToast && now - lastDutyDeniedToastUtc < DutyToastThrottle)
            return;

        lastDutyDeniedToastUtc = now;
        ToastGui.ShowError(DutyMainWindowDeniedMessage);
    }

    private void OpenMainUi(bool requestRefresh = false)
    {
        if (DenyMainWindowInDuty(forceToast: true))
            return;

        mainWindow.IsOpen = true;
        if (requestRefresh)
            ChocoboStatsService.RequestRefresh();
    }

    private void OnCommand(string command, string arguments)
    {
        var a = arguments.Trim();
        if (a.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ToggleConfigUi();
            return;
        }

        if (a.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            Configuration.PluginEnabled = true;
            Configuration.Save();
            UpdateDtrBar();
            return;
        }

        if (a.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            Configuration.PluginEnabled = false;
            Configuration.Save();
            UpdateDtrBar();
            return;
        }

        if (a.Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            if (DenyMainWindowInDuty(forceToast: true))
                return;

            mainWindow.IsOpen = true;
            ChocoboStatsService.RequestRefresh();
            return;
        }

        OpenMainUi(requestRefresh: true);
    }

    private void SetupDtrBar()
    {
        dtrEntry = DtrBar.Get(PluginInfo.DisplayName);
        dtrEntry.OnClick = _ => OpenMainUi();
    }

    public void UpdateDtrBar()
    {
        if (dtrEntry == null) return; dtrEntry.Shown = Configuration.DtrBarEnabled; if (!Configuration.DtrBarEnabled) return; var g = Configuration.PluginEnabled ? Configuration.DtrIconEnabled : Configuration.DtrIconDisabled; var s = IsAutomationRunning ? "Running" : Configuration.PluginEnabled ? "Ready" : "Idle"; dtrEntry.Text = Configuration.DtrBarMode switch { 1 => new SeString(new TextPayload($"{g} CA")), 2 => new SeString(new TextPayload(g)), _ => new SeString(new TextPayload("CA: " + s)), }; dtrEntry.Tooltip = new SeString(new TextPayload($"{PluginInfo.DisplayName} {s}. Click to open the main window."));
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (mainWindow.IsOpen)
            DenyMainWindowInDuty();

        ChocoboStatsService.Update();
        VendorPurchaseService.Update();
        StableFeedingService.Update();
        UpdateCleanupAutomation();

        UpdateDtrBar();
    }

    public FeedPurchasePlan BuildAutomationPlan()
        => ChocoboStatsService.BuildPurchasePlan(Configuration, capToSessions: true);

    private FeedPurchasePlan BuildCleanupPlan()
        => ChocoboStatsService.BuildPurchasePlan(Configuration, sessionCap: remainingSessionBudget);

    public void StopAutomation(bool printStatus = true)
    {
        cleanupMode = CleanupMode.None;
        cleanupPhase = CleanupPhase.Idle;
        cleanupRetryAtUtc = DateTime.MinValue;
        cleanupPassNumber = 0;
        remainingSessionBudget = 0;
        cleanupRetryReason = string.Empty;
        cleanupTerminalStatus = printStatus ? "Stopped manually." : "Idle";
        VendorPurchaseService.Reset();
        StableFeedingService.Reset();
        GameHelpers.StopMovement();
        if (printStatus)
            PrintStatus("Automation stopped.");
    }

    public void StartBuyOnly()
    {
        StopAutomation(printStatus: false);
        VendorPurchaseService.Start(BuildAutomationPlan());
    }

    public void StartFeedOnly()
    {
        StopAutomation(printStatus: false);
        StartCleanupAutomation(CleanupMode.FeedOnly);
    }

    public void StartFullCycle()
    {
        StopAutomation(printStatus: false);
        StartCleanupAutomation(CleanupMode.FullCycle);
    }

    private void StartCleanupAutomation(CleanupMode mode)
    {
        cleanupMode = mode;
        cleanupPhase = CleanupPhase.Idle;
        cleanupPassNumber = 0;
        remainingSessionBudget = ChocoboStatsService.Snapshot.IsLoaded
            ? (int)ChocoboStatsService.Snapshot.SessionsAvailable
            : 0;
        cleanupRetryReason = string.Empty;

        if (!ChocoboStatsService.Snapshot.IsLoaded)
        {
            FinishCleanup("Cleanup did not start: load racing chocobo data first.");
            return;
        }

        StartCleanupPass();
    }

    private void StartCleanupPass()
    {
        var remainingPlan = ChocoboStatsService.GetPlannedTrainingCount(Configuration);
        if (remainingPlan == 0)
        {
            FinishCleanup("Cleanup complete: no planned training remains.");
            return;
        }

        if (remainingSessionBudget == 0)
        {
            FinishCleanup($"Cleanup complete: session budget exhausted with {remainingPlan} planned training deferred.");
            return;
        }

        cleanupPassNumber++;
        cleanupRetryAtUtc = DateTime.MinValue;
        cleanupRetryReason = string.Empty;
        var plan = BuildCleanupPlan();

        Log.Information(
            $"[ChokeAbo] Starting cleanup pass {cleanupPassNumber}: confirmed feeds=0, remaining sessions={remainingSessionBudget}, remaining plan={remainingPlan}, retry reason=none.");

        if (cleanupMode == CleanupMode.FullCycle && plan.Entries.Any(entry => entry.QuantityToBuy > 0))
        {
            VendorPurchaseService.Reset();
            cleanupPhase = CleanupPhase.Purchasing;
            VendorPurchaseService.Start(plan);
            return;
        }

        StableFeedingService.Reset();
        cleanupPhase = CleanupPhase.Feeding;
        StableFeedingService.Start(plan);
    }

    private void UpdateCleanupAutomation()
    {
        if (cleanupMode == CleanupMode.None)
            return;

        switch (cleanupPhase)
        {
            case CleanupPhase.Purchasing when VendorPurchaseService.IsComplete:
                VendorPurchaseService.Reset();
                var plan = BuildCleanupPlan();
                cleanupPhase = CleanupPhase.Feeding;
                StableFeedingService.Start(plan);
                break;

            case CleanupPhase.Purchasing when VendorPurchaseService.IsFailed:
                ScheduleCleanupRetry($"Purchase pass failed: {VendorPurchaseService.StatusText}", confirmedFeeds: 0);
                break;

            case CleanupPhase.Feeding when StableFeedingService.IsComplete:
                FinishFeedingPass("Feeding pass completed.");
                break;

            case CleanupPhase.Feeding when StableFeedingService.IsFailed:
                FinishFeedingPass($"Feeding pass failed: {StableFeedingService.StatusText}");
                break;

            case CleanupPhase.WaitingToRetry when DateTime.UtcNow >= cleanupRetryAtUtc:
                VendorPurchaseService.Reset();
                StableFeedingService.Reset();
                StartCleanupPass();
                break;
        }
    }

    private void FinishFeedingPass(string retryReason)
    {
        var confirmedFeeds = StableFeedingService.ConfirmedFeedCount;
        remainingSessionBudget = Math.Max(0, remainingSessionBudget - confirmedFeeds);
        var remainingPlan = ChocoboStatsService.GetPlannedTrainingCount(Configuration);

        if (remainingPlan == 0)
        {
            LogCleanupPass(confirmedFeeds, remainingPlan, "remaining plan reached zero");
            FinishCleanup("Cleanup complete: no planned training remains.");
            return;
        }

        if (remainingSessionBudget == 0)
        {
            LogCleanupPass(confirmedFeeds, remainingPlan, "session budget reached zero");
            FinishCleanup($"Cleanup complete: session budget exhausted with {remainingPlan} planned training deferred.");
            return;
        }

        ScheduleCleanupRetry(retryReason, confirmedFeeds);
    }

    private void ScheduleCleanupRetry(string retryReason, int confirmedFeeds)
    {
        var remainingPlan = ChocoboStatsService.GetPlannedTrainingCount(Configuration);
        cleanupRetryReason = retryReason;
        cleanupRetryAtUtc = DateTime.UtcNow + CleanupRetryDelay;
        cleanupPhase = CleanupPhase.WaitingToRetry;
        GameHelpers.StopMovement();
        LogCleanupPass(confirmedFeeds, remainingPlan, retryReason);
    }

    private void LogCleanupPass(int confirmedFeeds, int remainingPlan, string retryReason)
    {
        Log.Information(
            $"[ChokeAbo] Cleanup pass {cleanupPassNumber}: confirmed feeds={confirmedFeeds}, remaining sessions={remainingSessionBudget}, remaining plan={remainingPlan}, retry reason={retryReason}");
    }

    private void FinishCleanup(string status)
    {
        cleanupMode = CleanupMode.None;
        cleanupPhase = CleanupPhase.Idle;
        cleanupRetryAtUtc = DateTime.MinValue;
        cleanupRetryReason = string.Empty;
        cleanupTerminalStatus = status;
        VendorPurchaseService.Reset();
        StableFeedingService.Reset();
        GameHelpers.StopMovement();
        Log.Information($"[ChokeAbo] {status}");
    }

    private double GetRetrySecondsRemaining()
        => Math.Max(0, (cleanupRetryAtUtc - DateTime.UtcNow).TotalSeconds);
}
