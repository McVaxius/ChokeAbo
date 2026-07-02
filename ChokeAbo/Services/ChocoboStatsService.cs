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

public readonly record struct ChocoboStatSnapshot(decimal Current, decimal Maximum, uint Stars);

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
    int Rank,
    int Rating,
    int PedigreeLevel,
    int ExperienceCurrent,
    int ExperienceMax,
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
            0,
            0,
            0,
            0,
            0,
            new ChocoboStatSnapshot(0, 0, 0),
            new ChocoboStatSnapshot(0, 0, 0),
            new ChocoboStatSnapshot(0, 0, 0),
            new ChocoboStatSnapshot(0, 0, 0),
            new ChocoboStatSnapshot(0, 0, 0),
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
    private static readonly TimeSpan GoldSaucerWindowTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FirstChocoboCallbackDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ChocoboCallbackStage2Delay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ChocoboCallbackFallbackDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ChocoboDataTimeout = TimeSpan.FromSeconds(30);
    private static readonly int[] GoldSaucerInfoChocoboCallbackPayloads = { 130, 131 };

    private readonly ICommandManager commandManager;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly InventoryService inventoryService;

    private RefreshState state = RefreshState.Idle;
    private DateTime stateEnteredAtUtc = DateTime.MinValue;
    private int chocoboCallbackPayloadIndex;

    public ChocoboTrainingSnapshot Snapshot { get; private set; } = ChocoboTrainingSnapshot.Empty("Use Refresh Chocobo Data or /chokeabo refresh to load racing chocobo stats.");

    public string StatusText => state switch
    {
        RefreshState.Idle => Snapshot.StatusText,
        RefreshState.PendingGoldSaucerCommand => "Refresh requested; opening Gold Saucer window...",
        RefreshState.WaitingForGoldSaucerWindow => "Opening Gold Saucer window...",
        RefreshState.WaitingBeforeChocoboCallback => "Gold Saucer Info ready; waiting before Chocobo tab callback...",
        RefreshState.WaitingForChocoboCallbackStage2 => "Opening racing chocobo page...",
        RefreshState.WaitingForChocoboData => "Waiting for racing chocobo data...",
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

    public bool ReadAvailableSnapshot()
        => TryCaptureSnapshot();

    public void RequestRefresh()
    {
        if (IsRefreshActive())
            return;

        log.Information("[ChokeAbo] Delayed GoldSaucerInfo Chocobo refresh requested; scheduling /goldsaucer on framework update.");
        chocoboCallbackPayloadIndex = 0;
        SetState(RefreshState.PendingGoldSaucerCommand);
        Snapshot = Snapshot with
        {
            StatusText = "Refresh requested; opening Gold Saucer window...",
        };
    }

    public void Update()
    {
        var elapsed = DateTime.UtcNow - stateEnteredAtUtc;
        switch (state)
        {
            case RefreshState.Idle:
            case RefreshState.Complete:
            case RefreshState.Failed:
                return;

            case RefreshState.PendingGoldSaucerCommand:
                log.Information("[ChokeAbo] Opening Gold Saucer window via /goldsaucer from framework update.");
                SetState(RefreshState.WaitingForGoldSaucerWindow);
                if (!SendChatCommand("/goldsaucer"))
                    Fail("The /goldsaucer command was not accepted.");
                break;

            case RefreshState.WaitingForGoldSaucerWindow:
                if (TryGetReadyGoldSaucerInfoAddon(out var addonName))
                {
                    log.Information($"[ChokeAbo] GoldSaucerInfo ready; waiting 2.0s before Chocobo callback. Ready addon: {addonName}.");
                    SetState(RefreshState.WaitingBeforeChocoboCallback);
                    Snapshot = Snapshot with
                    {
                        StatusText = "Gold Saucer Info ready; waiting before Chocobo tab callback.",
                    };
                }
                else if (elapsed > GoldSaucerWindowTimeout)
                {
                    Fail("Gold Saucer window did not appear in time.");
                }

                break;

            case RefreshState.WaitingBeforeChocoboCallback:
                if (elapsed < FirstChocoboCallbackDelay)
                    break;

                FireChocoboCallbackStage1();
                break;

            case RefreshState.WaitingForChocoboCallbackStage2:
                if (TryBeginChocoboDataWaitIfPageReady())
                    return;

                if (elapsed < ChocoboCallbackStage2Delay)
                    break;

                FireChocoboCallbackStage2();
                break;

            case RefreshState.WaitingForChocoboData:
                if (IsAddonReadyAndVisible("GSInfoChocoboParam"))
                {
                    Snapshot = Snapshot with
                    {
                        StatusText = "Racing chocobo page detected; waiting for RaceChocoboManager data.",
                    };
                }

                if (TryCaptureSnapshot())
                {
                    SetState(RefreshState.Complete);
                    Snapshot = Snapshot with
                    {
                        StatusText = $"Loaded from {Snapshot.Source} at {Snapshot.CapturedAtUtc.ToLocalTime():HH:mm:ss}.",
                    };
                    log.Information("[ChokeAbo] Racing chocobo stats refreshed from RaceChocoboManager after delayed GoldSaucerInfo Chocobo tab callback.");
                    return;
                }

                if (elapsed >= ChocoboCallbackFallbackDelay && TryFireNextChocoboCallbackPayload())
                    break;

                if (elapsed > ChocoboDataTimeout)
                {
                    Fail("RaceChocoboManager did not load within 30 seconds after delayed GoldSaucerInfo Chocobo callbacks.");
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
        var additionalPoints = Math.Max(0, plannedTrainings) * CalculateFeedGain(stat.Maximum, selectedGrade);
        return stat with
        {
            Current = Math.Min(stat.Maximum, stat.Current + additionalPoints),
        };
    }

    public static decimal CalculateFeedGain(decimal statCap, int selectedGrade)
        => statCap * GetGradePercent(selectedGrade) / 100m;

    private static int GetGradePercent(int selectedGrade)
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

    private unsafe bool TryCaptureSnapshot()
    {
        try
        {
            var manager = RaceChocoboManager.Instance();
            if (manager == null || manager->State != RaceChocoboManager.RaceChocoboState.Loaded)
                return false;

            RaceChocoboAttributeValues currentValues = default;
            RaceChocoboAttributeValues maximumValues = default;
            RaceChocoboAttributeValues starValues = default;
            manager->GetAttributesCurrent(&currentValues);
            manager->GetAttributesMaximum(&maximumValues);
            manager->GetAttributesStars(&starValues);
            var capturedAtUtc = DateTimeOffset.UtcNow;

            Snapshot = new ChocoboTrainingSnapshot(
                true,
                manager->SessionsAvailable,
                manager->Rank,
                manager->GetRating(),
                manager->GetPedigreeLevel(),
                manager->ExperienceCurrent,
                manager->ExperienceMax,
                new ChocoboStatSnapshot(currentValues.MaximumSpeed, maximumValues.MaximumSpeed, NormalizeStarCount(starValues.MaximumSpeed)),
                new ChocoboStatSnapshot(currentValues.Acceleration, maximumValues.Acceleration, NormalizeStarCount(starValues.Acceleration)),
                new ChocoboStatSnapshot(currentValues.Endurance, maximumValues.Endurance, NormalizeStarCount(starValues.Endurance)),
                new ChocoboStatSnapshot(currentValues.Stamina, maximumValues.Stamina, NormalizeStarCount(starValues.Stamina)),
                new ChocoboStatSnapshot(currentValues.Cunning, maximumValues.Cunning, NormalizeStarCount(starValues.Cunning)),
                "RaceChocoboManager",
                capturedAtUtc,
                $"Loaded from RaceChocoboManager at {capturedAtUtc.ToLocalTime():HH:mm:ss}.");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to read RaceChocoboManager stats: {ex.Message}");
            return false;
        }
    }

    private static uint NormalizeStarCount(uint rawStarValue)
        => rawStarValue + 1;

    private bool TryGetReadyGoldSaucerInfoAddon(out string addonName)
    {
        if (IsAddonReadyAndVisible("GoldSaucerInfo"))
        {
            addonName = "GoldSaucerInfo";
            return true;
        }

        if (IsAddonReadyAndVisible("AddonGoldSaucerInfo"))
        {
            addonName = "AddonGoldSaucerInfo";
            return true;
        }

        addonName = string.Empty;
        return false;
    }

    private bool TryBeginChocoboDataWaitIfPageReady()
    {
        if (!IsAddonReadyAndVisible("GSInfoChocoboParam"))
            return false;

        log.Information("[ChokeAbo] GSInfoChocoboParam is visible and ready; waiting for RaceChocoboManager data.");
        SetState(RefreshState.WaitingForChocoboData);
        Snapshot = Snapshot with
        {
            StatusText = "Racing chocobo page detected; waiting for RaceChocoboManager data.",
        };
        return true;
    }

    private void FireChocoboCallbackStage1()
    {
        var payload = CurrentChocoboCallbackPayload;
        var command = GameHelpers.FormatCallbackCommand("GoldSaucerInfo", true, 0, 1, payload);
        log.Information($"[ChokeAbo] Firing Gold Saucer Chocobo callback stage 1 with payload {payload}: {command}");

        if (!TryFireAddonCallback("GoldSaucerInfo", true, 0, 1, payload))
        {
            Fail("GoldSaucerInfo was not visible and ready for Chocobo callback stage 1; no further callbacks will be fired.");
            return;
        }

        SetState(RefreshState.WaitingForChocoboCallbackStage2);
        Snapshot = Snapshot with
        {
            StatusText = $"Chocobo callback stage 1 fired with payload {payload}; waiting before stage 2.",
        };
    }

    private void FireChocoboCallbackStage2()
    {
        var payload = CurrentChocoboCallbackPayload;
        var command = GameHelpers.FormatCallbackCommand("GoldSaucerInfo", true, 19, 0, payload);
        log.Information($"[ChokeAbo] Firing Gold Saucer Chocobo callback stage 2 with payload {payload}: {command}");

        if (!TryFireAddonCallback("GoldSaucerInfo", true, 19, 0, payload))
        {
            Fail("GoldSaucerInfo was not visible and ready for Chocobo callback stage 2; no further callbacks will be fired.");
            return;
        }

        SetState(RefreshState.WaitingForChocoboData);
        Snapshot = Snapshot with
        {
            StatusText = $"Chocobo callback stage 2 fired with payload {payload}; waiting for racing chocobo data.",
        };
    }

    private int CurrentChocoboCallbackPayload
        => GoldSaucerInfoChocoboCallbackPayloads[chocoboCallbackPayloadIndex];

    private bool TryFireNextChocoboCallbackPayload()
    {
        var previousPayload = CurrentChocoboCallbackPayload;
        if (chocoboCallbackPayloadIndex + 1 >= GoldSaucerInfoChocoboCallbackPayloads.Length)
            return false;

        chocoboCallbackPayloadIndex++;
        var fallbackPayload = CurrentChocoboCallbackPayload;
        log.Warning($"[ChokeAbo] Chocobo data was not available after payload {previousPayload}; trying GoldSaucerInfo fallback payload {fallbackPayload}.");
        Snapshot = Snapshot with
        {
            StatusText = $"Chocobo data was not available after payload {previousPayload}; trying payload {fallbackPayload} fallback.",
        };
        FireChocoboCallbackStage1();
        return true;
    }

    private unsafe bool IsAddonReadyAndVisible(string addonName)
        => TryGetReadyAddon(addonName, out _, logFailure: false);

    private unsafe bool TryFireAddonCallback(string addonName, bool updateState, params object[] args)
    {
        try
        {
            if (!TryGetReadyAddon(addonName, out var addon, logFailure: true))
                return false;

            var atkValues = new AtkValue[args.Length];
            for (var index = 0; index < args.Length; index++)
            {
                atkValues[index] = args[index] switch
                {
                    int intValue => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = intValue },
                    uint uintValue => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt, UInt = uintValue },
                    bool boolValue => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Bool, Byte = (byte)(boolValue ? 1 : 0) },
                    _ => new AtkValue { Type = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Int, Int = Convert.ToInt32(args[index]) },
                };
            }

            fixed (AtkValue* pointer = atkValues)
            {
                addon->FireCallback((uint)atkValues.Length, pointer, updateState);
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to fire callback on '{addonName}': {ex.Message}");
            return false;
        }
    }

    private unsafe bool TryGetReadyAddon(string addonName, out AtkUnitBase* addon, bool logFailure)
    {
        addon = null;
        try
        {
            nint addonPtr = gameGui.GetAddonByName(addonName, 1);
            if (addonPtr != nint.Zero)
            {
                var gameGuiAddon = (AtkUnitBase*)addonPtr;
                if (gameGuiAddon->IsVisible && gameGuiAddon->IsReady)
                {
                    addon = gameGuiAddon;
                    return true;
                }
            }

            var fallbackAddon = Instance()->GetAddonByName(addonName);
            if (fallbackAddon != null && fallbackAddon->IsVisible && fallbackAddon->IsReady)
            {
                addon = fallbackAddon;
                return true;
            }

            if (logFailure)
                log.Warning($"[ChokeAbo] Addon '{addonName}' is not visible and ready for callback.");

            return false;
        }
        catch (Exception ex)
        {
            if (logFailure)
                log.Warning($"[ChokeAbo] Failed to resolve addon '{addonName}': {ex.Message}");

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

    private bool IsRefreshActive()
        => state is RefreshState.PendingGoldSaucerCommand
            or RefreshState.WaitingForGoldSaucerWindow
            or RefreshState.WaitingBeforeChocoboCallback
            or RefreshState.WaitingForChocoboCallbackStage2
            or RefreshState.WaitingForChocoboData;

    private void SetState(RefreshState newState)
    {
        state = newState;
        stateEnteredAtUtc = DateTime.UtcNow;
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
        PendingGoldSaucerCommand,
        WaitingForGoldSaucerWindow,
        WaitingBeforeChocoboCallback,
        WaitingForChocoboCallbackStage2,
        WaitingForChocoboData,
        Complete,
        Failed,
    }
}
