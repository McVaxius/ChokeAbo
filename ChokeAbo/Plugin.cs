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
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }
    internal ChocoboStatsService ChocoboStatsService { get; }
    internal InventoryService InventoryService { get; }
    internal VendorPurchaseService VendorPurchaseService { get; }
    internal StableFeedingService StableFeedingService { get; }
    public WindowSystem WindowSystem { get; } = new(PluginInfo.InternalName);
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private IDtrBarEntry? dtrEntry;
    private FeedPurchasePlan? pendingFeedPlanAfterBuy;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        InventoryService = new GameInventoryService(DataManager, Log);
        ChocoboStatsService = new ChocoboStatsService(CommandManager, GameGui, Log, InventoryService);
        VendorPurchaseService = new VendorPurchaseService(InventoryService, Log);
        StableFeedingService = new StableFeedingService(InventoryService, GameGui, Log);
        mainWindow = new MainWindow(this, ChocoboStatsService);
        configWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(configWindow);
        CommandManager.AddHandler(PluginInfo.Command, new CommandInfo(OnCommand) { HelpMessage = $"Open {PluginInfo.DisplayName}. Use {PluginInfo.Command} config for settings." });
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;
        SetupDtrBar();
        UpdateDtrBar();
        Log.Information("[Choke-abo] Plugin loaded.");
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

    public void ToggleMainUi() => mainWindow.Toggle();
    public void ToggleConfigUi() => configWindow.Toggle();
    public void PrintStatus(string m) => ChatGui.Print($"[{PluginInfo.DisplayName}] {m}");
    public bool IsAutomationRunning => VendorPurchaseService.IsRunning || StableFeedingService.IsRunning || pendingFeedPlanAfterBuy != null;

    private void OnCommand(string command, string arguments)
    {
        var a = arguments.Trim(); if (a.Equals("config", StringComparison.OrdinalIgnoreCase)) { ToggleConfigUi(); return; } if (a.Equals("on", StringComparison.OrdinalIgnoreCase)) { Configuration.PluginEnabled = true; Configuration.Save(); UpdateDtrBar(); return; } if (a.Equals("off", StringComparison.OrdinalIgnoreCase)) { Configuration.PluginEnabled = false; Configuration.Save(); UpdateDtrBar(); return; } ToggleMainUi();
    }

    private void SetupDtrBar()
    {
        dtrEntry = DtrBar.Get(PluginInfo.DisplayName);
        dtrEntry.OnClick = _ => mainWindow.IsOpen = true;
    }

    public void UpdateDtrBar()
    {
        if (dtrEntry == null) return; dtrEntry.Shown = Configuration.DtrBarEnabled; if (!Configuration.DtrBarEnabled) return; var g = Configuration.PluginEnabled ? Configuration.DtrIconEnabled : Configuration.DtrIconDisabled; var s = IsAutomationRunning ? "Running" : Configuration.PluginEnabled ? "Ready" : "Idle"; dtrEntry.Text = Configuration.DtrBarMode switch { 1 => new SeString(new TextPayload($"{g} CA")), 2 => new SeString(new TextPayload(g)), _ => new SeString(new TextPayload("CA: " + s)), }; dtrEntry.Tooltip = new SeString(new TextPayload($"{PluginInfo.DisplayName} {s}. Click to open the main window."));
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        ChocoboStatsService.Update();
        VendorPurchaseService.Update();
        StableFeedingService.Update();

        if (pendingFeedPlanAfterBuy != null)
        {
            if (VendorPurchaseService.IsComplete)
            {
                var pendingPlan = pendingFeedPlanAfterBuy;
                pendingFeedPlanAfterBuy = null;
                StableFeedingService.Start(pendingPlan);
            }
            else if (VendorPurchaseService.IsFailed)
            {
                pendingFeedPlanAfterBuy = null;
            }
        }

        UpdateDtrBar();
    }

    public FeedPurchasePlan BuildAutomationPlan()
        => ChocoboStatsService.BuildPurchasePlan(Configuration, capToSessions: true);

    public void StopAutomation(bool printStatus = true)
    {
        pendingFeedPlanAfterBuy = null;
        VendorPurchaseService.Reset();
        StableFeedingService.Reset();
        GameHelpers.StopMovement();
        if (printStatus)
            PrintStatus("Automation stopped.");
    }

    public void StartBuyOnly()
    {
        StopAutomation(printStatus: false);
        pendingFeedPlanAfterBuy = null;
        VendorPurchaseService.Start(BuildAutomationPlan());
    }

    public void StartFeedOnly()
    {
        StopAutomation(printStatus: false);
        pendingFeedPlanAfterBuy = null;
        StableFeedingService.Start(BuildAutomationPlan());
    }

    public void StartFullCycle()
    {
        StopAutomation(printStatus: false);
        var plan = BuildAutomationPlan();

        if (plan.Entries.Any(entry => entry.QuantityToBuy > 0))
        {
            pendingFeedPlanAfterBuy = plan;
            VendorPurchaseService.Start(plan);
            return;
        }

        pendingFeedPlanAfterBuy = null;
        StableFeedingService.Start(plan);
    }
}
