using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Text;
using static FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager;

namespace ChokeAbo.Services;

public enum ChocoboStatKind
{
    MaximumSpeed,
    Acceleration,
    Endurance,
    Stamina,
    Cunning,
}

public enum FeedCurrencyKind
{
    Gil,
    Mgp,
}

public readonly record struct ChocoboStatSnapshot(uint Current, uint Maximum);

public sealed record FeedPurchaseEntry(
    ChocoboStatKind StatKind,
    string StatLabel,
    string FeedName,
    uint ItemId,
    int PlannedQuantity,
    uint OnHandQuantity,
    int QuantityToBuy,
    int Grade,
    FeedCurrencyKind CurrencyKind,
    string VendorCategoryLabel,
    int VendorCategoryIndex,
    string ExpectedAddonName,
    int VendorCallbackGroup,
    int VendorCallbackIndex,
    uint UnitCost)
{
    public uint TotalCost => (uint)Math.Max(0, QuantityToBuy) * UnitCost;
}

public sealed record FeedPurchasePlan(
    IReadOnlyList<FeedPurchaseEntry> Entries,
    uint CurrentGil,
    uint CurrentMgp,
    uint TotalGil,
    uint TotalMgp,
    int PlannedTrainings,
    int SessionsAvailable,
    bool IsLoaded)
{
    public bool CanAffordGil => CurrentGil >= TotalGil;
    public bool CanAffordMgp => CurrentMgp >= TotalMgp;
}

public sealed record ChocoboTrainingSnapshot(
    bool IsLoaded,
    uint SessionsAvailable,
    ChocoboStatSnapshot MaximumSpeed,
    ChocoboStatSnapshot Acceleration,
    ChocoboStatSnapshot Endurance,
    ChocoboStatSnapshot Stamina,
    ChocoboStatSnapshot Cunning,
    string Source,
    DateTimeOffset CapturedAtUtc,
    string StatusText)
{
    public static ChocoboTrainingSnapshot Empty(string statusText)
        => new(
            false,
            0,
            new ChocoboStatSnapshot(0, 0),
            new ChocoboStatSnapshot(0, 0),
            new ChocoboStatSnapshot(0, 0),
            new ChocoboStatSnapshot(0, 0),
            new ChocoboStatSnapshot(0, 0),
            "Unavailable",
            DateTimeOffset.MinValue,
            statusText);

    public ChocoboStatSnapshot GetStat(ChocoboStatKind statKind)
        => statKind switch
        {
            ChocoboStatKind.MaximumSpeed => MaximumSpeed,
            ChocoboStatKind.Acceleration => Acceleration,
            ChocoboStatKind.Endurance => Endurance,
            ChocoboStatKind.Stamina => Stamina,
            ChocoboStatKind.Cunning => Cunning,
            _ => MaximumSpeed,
        };
}

public sealed class ChocoboStatsService
{
    private const uint GilItemId = 1;
    private const uint MgpItemId = 29;
    private const uint Grade1UnitCost = 1500;
    private const uint Grade2UnitCost = 610;
    private const uint Grade3UnitCost = 1345;

    private readonly ICommandManager commandManager;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly InventoryService inventoryService;

    private RefreshState state = RefreshState.Idle;
    private DateTime stateEnteredAtUtc = DateTime.MinValue;
    private DateTime lastActionAtUtc = DateTime.MinValue;
    private int chocoboCallbackStage;
    private int chocoboCallbackAttempts;

    public ChocoboTrainingSnapshot Snapshot { get; private set; } = ChocoboTrainingSnapshot.Empty("Open the main window to refresh racing chocobo stats.");

    public string StatusText => state switch
    {
        RefreshState.Idle => Snapshot.StatusText,
        RefreshState.WaitingForGoldSaucerWindow => "Opening Gold Saucer window...",
        RefreshState.WaitingForChocoboWindow => "Opening racing chocobo page...",
        RefreshState.Complete => Snapshot.StatusText,
        RefreshState.Failed => Snapshot.StatusText,
        _ => Snapshot.StatusText,
    };

    public ChocoboStatsService(ICommandManager commandManager, IGameGui gameGui, IPluginLog log, InventoryService inventoryService)
    {
        this.commandManager = commandManager;
        this.gameGui = gameGui;
        this.log = log;
        this.inventoryService = inventoryService;
    }

    public void RequestRefresh()
    {
        TryCaptureSnapshot();

        if (state is RefreshState.WaitingForGoldSaucerWindow or RefreshState.WaitingForChocoboWindow)
            return;

        log.Information("[ChokeAbo] Refreshing racing chocobo stats via Gold Saucer window.");
        chocoboCallbackStage = 0;
        chocoboCallbackAttempts = 0;
        SetState(RefreshState.WaitingForGoldSaucerWindow);
        if (!SendChatCommand("/goldsaucer"))
            Fail("The /goldsaucer command was not accepted.");
    }

    public void Update()
    {
        TryCaptureSnapshot();

        var elapsed = (DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds;
        switch (state)
        {
            case RefreshState.Idle:
            case RefreshState.Complete:
            case RefreshState.Failed:
                return;

            case RefreshState.WaitingForGoldSaucerWindow:
                if (IsAddonVisible("GoldSaucerInfo"))
                {
                    log.Information("[ChokeAbo] GoldSaucerInfo detected, opening Chocobo page.");
                    SetState(RefreshState.WaitingForChocoboWindow);
                }
                else if (elapsed > 10)
                {
                    Fail("Gold Saucer window did not appear in time.");
                }

                break;

            case RefreshState.WaitingForChocoboWindow:
                if (IsAddonVisible("GSInfoChocoboParam"))
                {
                    TryCaptureSnapshot();
                }

                if (chocoboCallbackStage == 0 && (DateTime.UtcNow - lastActionAtUtc).TotalSeconds >= 0.4)
                {
                    chocoboCallbackStage = 1;
                    log.Information("[ChokeAbo] Firing Gold Saucer Chocobo callback stage 1.");
                    FireAddonCallback("GoldSaucerInfo", true, 0, 1, 123);
                    lastActionAtUtc = DateTime.UtcNow;
                }
                else if (chocoboCallbackStage == 1 && (DateTime.UtcNow - lastActionAtUtc).TotalSeconds >= 0.5)
                {
                    chocoboCallbackStage = 2;
                    log.Information("[ChokeAbo] Firing Gold Saucer Chocobo callback stage 2.");
                    FireAddonCallback("GoldSaucerInfo", true, 19, 0, 123);
                    lastActionAtUtc = DateTime.UtcNow;
                }
                else if (chocoboCallbackStage == 2 &&
                         !IsAddonVisible("GSInfoChocoboParam") &&
                         chocoboCallbackAttempts < 1 &&
                         (DateTime.UtcNow - lastActionAtUtc).TotalSeconds >= 2.0)
                {
                    chocoboCallbackAttempts++;
                    chocoboCallbackStage = 0;
                    log.Warning("[ChokeAbo] Gold Saucer Chocobo page did not open on the first try, retrying callbacks once.");
                    lastActionAtUtc = DateTime.UtcNow;
                }

                if (Snapshot.IsLoaded && Snapshot.CapturedAtUtc > DateTimeOffset.MinValue)
                {
                    SetState(RefreshState.Complete);
                    Snapshot = Snapshot with
                    {
                        StatusText = $"Loaded from {Snapshot.Source} at {Snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss}.",
                    };
                }
                else if (elapsed > 12)
                {
                    Fail("Racing chocobo stats did not load. Open the Chocobo page manually and try again.");
                }

                break;
        }
    }

    public ChocoboTrainingSnapshot BuildProjection(Configuration configuration)
    {
        var snapshot = Snapshot;
        if (!snapshot.IsLoaded)
            return snapshot;

        return snapshot with
        {
            MaximumSpeed = BuildProjectedStat(snapshot.MaximumSpeed, configuration.PlannedMaximumSpeedTrainings, configuration.MaximumSpeedFeedGrade),
            Acceleration = BuildProjectedStat(snapshot.Acceleration, configuration.PlannedAccelerationTrainings, configuration.AccelerationFeedGrade),
            Endurance = BuildProjectedStat(snapshot.Endurance, configuration.PlannedEnduranceTrainings, configuration.EnduranceFeedGrade),
            Stamina = BuildProjectedStat(snapshot.Stamina, configuration.PlannedStaminaTrainings, configuration.StaminaFeedGrade),
            Cunning = BuildProjectedStat(snapshot.Cunning, configuration.PlannedCunningTrainings, configuration.CunningFeedGrade),
            SessionsAvailable = snapshot.SessionsAvailable,
            Source = "Projected",
            StatusText = $"Projected from current stats using {GetPlannedTrainingCount(configuration)} planned trainings.",
        };
    }

    public FeedPurchasePlan BuildPurchasePlan(Configuration configuration, bool capToSessions = false)
    {
        var entries = new List<FeedPurchaseEntry>();
        var remainingSessions = capToSessions && Snapshot.IsLoaded
            ? (int)Snapshot.SessionsAvailable
            : int.MaxValue;

        AddPurchaseEntry(entries, ChocoboStatKind.MaximumSpeed, "Maximum Speed", configuration.PlannedMaximumSpeedTrainings, configuration.MaximumSpeedFeedGrade, ref remainingSessions);
        AddPurchaseEntry(entries, ChocoboStatKind.Acceleration, "Acceleration", configuration.PlannedAccelerationTrainings, configuration.AccelerationFeedGrade, ref remainingSessions);
        AddPurchaseEntry(entries, ChocoboStatKind.Endurance, "Endurance", configuration.PlannedEnduranceTrainings, configuration.EnduranceFeedGrade, ref remainingSessions);
        AddPurchaseEntry(entries, ChocoboStatKind.Stamina, "Stamina", configuration.PlannedStaminaTrainings, configuration.StaminaFeedGrade, ref remainingSessions);
        AddPurchaseEntry(entries, ChocoboStatKind.Cunning, "Cunning", configuration.PlannedCunningTrainings, configuration.CunningFeedGrade, ref remainingSessions);

        var totalGil = entries
            .Where(entry => entry.CurrencyKind == FeedCurrencyKind.Gil)
            .Aggregate(0u, (total, entry) => total + entry.TotalCost);
        var totalMgp = entries
            .Where(entry => entry.CurrencyKind == FeedCurrencyKind.Mgp)
            .Aggregate(0u, (total, entry) => total + entry.TotalCost);

        return new FeedPurchasePlan(
            entries,
            GetCurrencyBalance(GilItemId),
            GetCurrencyBalance(MgpItemId),
            totalGil,
            totalMgp,
            GetPlannedTrainingCount(configuration),
            (int)Snapshot.SessionsAvailable,
            Snapshot.IsLoaded);
    }

    public static int GetPlannedTrainingCount(Configuration configuration)
        => Math.Max(0, configuration.PlannedMaximumSpeedTrainings) +
           Math.Max(0, configuration.PlannedAccelerationTrainings) +
           Math.Max(0, configuration.PlannedEnduranceTrainings) +
           Math.Max(0, configuration.PlannedStaminaTrainings) +
           Math.Max(0, configuration.PlannedCunningTrainings);

    private static ChocoboStatSnapshot BuildProjectedStat(ChocoboStatSnapshot stat, int plannedTrainings, int selectedGrade)
    {
        var additionalPoints = (uint)(Math.Max(0, plannedTrainings) * GetGradeStrength(selectedGrade));
        return stat with
        {
            Current = Math.Min(stat.Maximum, stat.Current + additionalPoints),
        };
    }

    private static int GetGradeStrength(int selectedGrade)
        => Math.Clamp(selectedGrade, 1, 3);

    private void AddPurchaseEntry(
        ICollection<FeedPurchaseEntry> entries,
        ChocoboStatKind statKind,
        string statLabel,
        int plannedTrainings,
        int selectedGrade,
        ref int remainingSessions)
    {
        var quantity = Math.Max(0, plannedTrainings);
        if (remainingSessions != int.MaxValue)
        {
            quantity = Math.Min(quantity, Math.Max(0, remainingSessions));
            remainingSessions -= quantity;
        }

        if (quantity <= 0)
            return;

        var grade = Math.Clamp(selectedGrade, 1, 3);
        var definition = FeedCatalog.Get(statKind, grade);
        var itemId = inventoryService.ResolveItemId(definition.FeedName);
        var onHandQuantity = inventoryService.GetItemCount(itemId);
        var quantityToBuy = Math.Max(0, quantity - (int)onHandQuantity);

        entries.Add(new FeedPurchaseEntry(
            statKind,
            statLabel,
            definition.FeedName,
            itemId,
            quantity,
            onHandQuantity,
            quantityToBuy,
            grade,
            definition.CurrencyKind,
            definition.VendorCategoryLabel,
            definition.VendorCategoryIndex,
            definition.ExpectedAddonName,
            definition.VendorCallbackGroup,
            definition.VendorCallbackIndex,
            definition.UnitCost));
    }

    private unsafe uint GetCurrencyBalance(uint itemId)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return 0;

            return (uint)Math.Max(0, manager->GetInventoryItemCount(itemId) + manager->GetInventoryItemCount(itemId, true));
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to read currency {itemId}: {ex.Message}");
            return 0;
        }
    }

    private unsafe void TryCaptureSnapshot()
    {
        try
        {
            var manager = RaceChocoboManager.Instance();
            if (manager == null || manager->State != RaceChocoboManager.RaceChocoboState.Loaded)
                return;

            RaceChocoboAttributeValues currentValues = default;
            RaceChocoboAttributeValues maximumValues = default;
            manager->GetAttributesCurrent(&currentValues);
            manager->GetAttributesMaximum(&maximumValues);

            Snapshot = new ChocoboTrainingSnapshot(
                true,
                manager->SessionsAvailable,
                new ChocoboStatSnapshot(currentValues.MaximumSpeed, maximumValues.MaximumSpeed),
                new ChocoboStatSnapshot(currentValues.Acceleration, maximumValues.Acceleration),
                new ChocoboStatSnapshot(currentValues.Endurance, maximumValues.Endurance),
                new ChocoboStatSnapshot(currentValues.Stamina, maximumValues.Stamina),
                new ChocoboStatSnapshot(currentValues.Cunning, maximumValues.Cunning),
                "RaceChocoboManager",
                DateTimeOffset.UtcNow,
                $"Loaded from RaceChocoboManager at {DateTimeOffset.UtcNow.ToLocalTime():HH:mm:ss}.");
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to read RaceChocoboManager stats: {ex.Message}");
        }
    }

    private unsafe bool IsAddonVisible(string addonName)
    {
        try
        {
            nint addonPtr = gameGui.GetAddonByName(addonName, 1);
            if (addonPtr != nint.Zero)
            {
                var addon = (AtkUnitBase*)addonPtr;
                return addon->IsVisible;
            }

            var fallbackAddon = Instance()->GetAddonByName(addonName);
            return fallbackAddon != null && fallbackAddon->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool SendChatCommand(string command)
    {
        try
        {
            if (commandManager.ProcessCommand(command))
                return true;

            var uiModule = UIModule.Instance();
            if (uiModule == null)
                return false;

            var bytes = Encoding.UTF8.GetBytes(command);
            var utf8 = Utf8String.FromSequence(bytes);
            uiModule->ProcessChatBoxEntry(utf8, nint.Zero);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to send '{command}': {ex.Message}");
            return false;
        }
    }

    private unsafe void FireAddonCallback(string addonName, bool updateState, params object[] args)
    {
        try
        {
            AtkUnitBase* addon = null;
            nint addonPtr = gameGui.GetAddonByName(addonName, 1);
            if (addonPtr != nint.Zero)
                addon = (AtkUnitBase*)addonPtr;

            if (addon == null)
                addon = Instance()->GetAddonByName(addonName);

            if (addon == null || !addon->IsVisible)
            {
                log.Warning($"[ChokeAbo] Addon '{addonName}' is not visible for callback.");
                return;
            }

            var atkValues = new AtkValue[args.Length];
            for (var index = 0; index < args.Length; index++)
            {
                atkValues[index] = args[index] switch
                {
                    int intValue => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = intValue },
                    uint uintValue => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = uintValue },
                    bool boolValue => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool, Byte = (byte)(boolValue ? 1 : 0) },
                    _ => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = Convert.ToInt32(args[index]) },
                };
            }

            fixed (AtkValue* pointer = atkValues)
            {
                addon->FireCallback((uint)atkValues.Length, pointer, updateState);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to fire callback on '{addonName}': {ex.Message}");
        }
    }

    private void SetState(RefreshState newState)
    {
        state = newState;
        stateEnteredAtUtc = DateTime.UtcNow;
        lastActionAtUtc = DateTime.UtcNow;
    }

    private void Fail(string message)
    {
        SetState(RefreshState.Failed);
        Snapshot = Snapshot with
        {
            StatusText = message,
        };
        log.Warning($"[ChokeAbo] {message}");
    }

    private enum RefreshState
    {
        Idle,
        WaitingForGoldSaucerWindow,
        WaitingForChocoboWindow,
        Complete,
        Failed,
    }
}
