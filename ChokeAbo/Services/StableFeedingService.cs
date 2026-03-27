using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChokeAbo.Services;

public sealed class StableFeedingService
{
    private const string TrainerName = "Race Chocobo Trainer";
    private const float TrainerStopDistance = 3.0f;

    private static readonly string[] InventoryAddonNames =
    {
        "InventoryExpansion",
        "InventoryLarge",
        "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E",
        "InventoryGrid0", "InventoryGrid1", "InventoryGrid2", "InventoryGrid3",
        "InventoryBuddy", "InventoryBuddy2",
    };

    private readonly InventoryService inventoryService;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private FeedState state = FeedState.Idle;
    private IReadOnlyList<FeedPurchaseEntry> queue = Array.Empty<FeedPurchaseEntry>();
    private int currentEntryIndex;
    private int remainingFeedsForCurrentEntry;
    private DateTime stateEnteredAtUtc = DateTime.MinValue;
    private DateTime lastInteractionAtUtc = DateTime.MinValue;
    private InventoryType currentContainer;
    private int currentSlot;
    private uint preFeedCount;
    private string lastError = string.Empty;

    private enum FeedState
    {
        Idle,
        TravelingToChocoboSquare,
        NavigatingToTrainer,
        InteractingTrainer,
        WaitingForInventory,
        FindingFeedSlot,
        OpeningContextMenu,
        WaitingForContextMenu,
        SelectingFeed,
        WaitingForCommenceWindow,
        WaitingForCommenceConfirmation,
        WaitingForFeedResolution,
        Complete,
        Failed,
    }

    public StableFeedingService(InventoryService inventoryService, IGameGui gameGui, IPluginLog log)
    {
        this.inventoryService = inventoryService;
        this.gameGui = gameGui;
        this.log = log;
    }

    public string Summary => "Automates the Race Chocobo Trainer feed flow with travel, staging, and inventory-context feeding.";
    public bool IsRunning => state is not FeedState.Idle and not FeedState.Complete and not FeedState.Failed;
    public bool IsComplete => state == FeedState.Complete;
    public bool IsFailed => state == FeedState.Failed;
    public string StatusText => state switch
    {
        FeedState.Idle => "Idle",
        FeedState.TravelingToChocoboSquare => "Traveling to Chocobo Square...",
        FeedState.NavigatingToTrainer => "Moving to Race Chocobo Trainer...",
        FeedState.InteractingTrainer => "Opening Race Chocobo Trainer...",
        FeedState.WaitingForInventory => "Waiting for feed inventory...",
        FeedState.FindingFeedSlot => CurrentEntry is { } entry ? $"Finding {entry.FeedName}..." : "Finding feed...",
        FeedState.OpeningContextMenu => "Opening inventory context menu...",
        FeedState.WaitingForContextMenu => "Waiting for Feed context menu...",
        FeedState.SelectingFeed => "Selecting Feed...",
        FeedState.WaitingForCommenceWindow => "Waiting for Commence...",
        FeedState.WaitingForCommenceConfirmation => "Confirming training...",
        FeedState.WaitingForFeedResolution => "Waiting for training to complete...",
        FeedState.Complete => "Feed cycle complete.",
        FeedState.Failed => string.IsNullOrWhiteSpace(lastError) ? "Feed cycle failed." : lastError,
        _ => state.ToString(),
    };

    private FeedPurchaseEntry? CurrentEntry => currentEntryIndex >= 0 && currentEntryIndex < queue.Count ? queue[currentEntryIndex] : null;

    public void Start(FeedPurchasePlan plan)
    {
        queue = plan.Entries
            .Where(entry => entry.PlannedQuantity > 0)
            .ToArray();

        currentEntryIndex = 0;
        remainingFeedsForCurrentEntry = queue.Count > 0 ? queue[0].PlannedQuantity : 0;
        lastError = string.Empty;
        lastInteractionAtUtc = DateTime.MinValue;

        if (queue.Count == 0)
        {
            state = FeedState.Complete;
            return;
        }

        log.Information($"[ChokeAbo] Starting feed cycle for {queue.Sum(entry => entry.PlannedQuantity)} total feed actions.");
        SetState(GameHelpers.IsInChocoboSquare()
            ? FeedState.NavigatingToTrainer
            : FeedState.TravelingToChocoboSquare);
    }

    public void Reset()
    {
        GameHelpers.StopMovement();
        queue = Array.Empty<FeedPurchaseEntry>();
        currentEntryIndex = 0;
        remainingFeedsForCurrentEntry = 0;
        currentSlot = 0;
        preFeedCount = 0;
        lastInteractionAtUtc = DateTime.MinValue;
        lastError = string.Empty;
        state = FeedState.Idle;
        stateEnteredAtUtc = DateTime.MinValue;
    }

    public void Update()
    {
        if (state is FeedState.Idle or FeedState.Complete or FeedState.Failed)
            return;

        switch (state)
        {
            case FeedState.TravelingToChocoboSquare:
                HandleTravelingToChocoboSquare();
                break;
            case FeedState.NavigatingToTrainer:
                HandleNavigatingToTrainer();
                break;
            case FeedState.InteractingTrainer:
                HandleInteractingTrainer();
                break;
            case FeedState.WaitingForInventory:
                HandleWaitingForInventory();
                break;
            case FeedState.FindingFeedSlot:
                HandleFindingFeedSlot();
                break;
            case FeedState.OpeningContextMenu:
                HandleOpeningContextMenu();
                break;
            case FeedState.WaitingForContextMenu:
                HandleWaitingForContextMenu();
                break;
            case FeedState.SelectingFeed:
                HandleSelectingFeed();
                break;
            case FeedState.WaitingForCommenceWindow:
                HandleWaitingForCommenceWindow();
                break;
            case FeedState.WaitingForCommenceConfirmation:
                HandleWaitingForCommenceConfirmation();
                break;
            case FeedState.WaitingForFeedResolution:
                HandleWaitingForFeedResolution();
                break;
        }
    }

    private void HandleTravelingToChocoboSquare()
    {
        if (GameHelpers.IsInChocoboSquare())
        {
            SetState(FeedState.NavigatingToTrainer);
            return;
        }

        if (GameHelpers.IsLifestreamBusy())
            return;

        if ((DateTime.UtcNow - lastInteractionAtUtc).TotalSeconds >= 3)
        {
            lastInteractionAtUtc = DateTime.UtcNow;
            if (!GameHelpers.TryTravelToChocoboSquare() &&
                (DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 10)
            {
                Fail("Could not send /li chocobo. Check Lifestream or move there manually.");
                return;
            }

            log.Information("[ChokeAbo] Traveling to Chocobo Square for trainer feeding.");
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 45)
            Fail("Timed out waiting to arrive in Chocobo Square.");
    }

    private void HandleNavigatingToTrainer()
    {
        if (!GameHelpers.IsInChocoboSquare())
        {
            SetState(FeedState.TravelingToChocoboSquare);
            return;
        }

        if (GameHelpers.IsNearObject(TrainerName))
        {
            GameHelpers.StopMovement();
            SetState(FeedState.InteractingTrainer);
            return;
        }

        if (GameHelpers.FindObjectByName(TrainerName) == null)
        {
            if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 8)
                Fail("Could not find Race Chocobo Trainer in Chocobo Square.");
            return;
        }

        if (!GameHelpers.IsMovementRunning() &&
            !GameHelpers.TryMoveCloseTo(TrainerName, TrainerStopDistance) &&
            (DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 10)
        {
            Fail("Could not path near Race Chocobo Trainer. Move closer or check vnavmesh.");
            return;
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 20)
            Fail("Timed out moving near Race Chocobo Trainer.");
    }

    private void HandleInteractingTrainer()
    {
        if (CurrentEntry == null)
        {
            Complete();
            return;
        }

        if (IsAnyInventoryAddonVisible())
        {
            SetState(FeedState.FindingFeedSlot);
            return;
        }

        if (!GameHelpers.IsNearObject(TrainerName))
        {
            SetState(FeedState.NavigatingToTrainer);
            return;
        }

        if (GameHelpers.IsAddonVisible("SelectIconString"))
        {
            log.Information($"[ChokeAbo] Opening trainer feed inventory via {GameHelpers.FormatCallbackCommand("SelectIconString", true, 0)}");
            GameHelpers.FireAddonCallback("SelectIconString", true, 0);
            SetState(FeedState.WaitingForInventory);
            return;
        }

        if ((DateTime.UtcNow - lastInteractionAtUtc).TotalSeconds >= 2.5)
        {
            lastInteractionAtUtc = DateTime.UtcNow;
            if (GameHelpers.TargetAndInteract(TrainerName))
                return;

            if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 12)
                Fail("Could not open Race Chocobo Trainer. Move closer and try again.");
        }
    }

    private void HandleWaitingForInventory()
    {
        if (IsAnyInventoryAddonVisible())
        {
            SetState(FeedState.FindingFeedSlot);
            return;
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 8)
            Fail("Trainer feed inventory did not open after selecting the trainer menu entry.");
    }

    private void HandleFindingFeedSlot()
    {
        var current = CurrentEntry;
        if (current == null)
        {
            Complete();
            return;
        }

        preFeedCount = inventoryService.GetItemCount(current.ItemId);
        if (preFeedCount == 0)
        {
            Fail($"Missing {current.FeedName} in inventory.");
            return;
        }

        var slot = inventoryService.FindItemSlot(current.ItemId);
        if (slot == null)
        {
            Fail($"Could not locate {current.FeedName} in the main inventory pages.");
            return;
        }

        currentContainer = slot.Value.container;
        currentSlot = slot.Value.slot;
        SetState(FeedState.OpeningContextMenu);
    }

    private unsafe void HandleOpeningContextMenu()
    {
        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds < 0.4)
            return;

        var addonId = GetVisibleInventoryAddonId();
        if (addonId == 0)
        {
            Fail("No visible inventory window was found for trainer feeding.");
            return;
        }

        var agent = AgentInventoryContext.Instance();
        agent->OpenForItemSlot(currentContainer, currentSlot, 0, addonId);
        SetState(FeedState.WaitingForContextMenu);
    }

    private void HandleWaitingForContextMenu()
    {
        if (GameHelpers.IsAddonVisible("ContextMenu"))
        {
            SetState(FeedState.SelectingFeed);
            return;
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 6)
            Fail("Context menu did not appear for the selected feed item.");
    }

    private void HandleSelectingFeed()
    {
        if (!GameHelpers.IsAddonVisible("ContextMenu"))
        {
            Fail("Context menu disappeared before Feed could be selected.");
            return;
        }

        log.Information($"[ChokeAbo] Selecting inventory Feed via {GameHelpers.FormatCallbackCommand("ContextMenu", true, 0, 0, 0u, 0, 0)}");
        GameHelpers.FireAddonCallback("ContextMenu", true, 0, 0, 0u, 0, 0);
        SetState(FeedState.WaitingForCommenceWindow);
    }

    private void HandleWaitingForCommenceWindow()
    {
        if (GameHelpers.IsAddonVisible("ChocoboBreedTraining"))
        {
            log.Information($"[ChokeAbo] Starting training cutscene via {GameHelpers.FormatCallbackCommand("ChocoboBreedTraining", true, 0)}");
            GameHelpers.FireAddonCallback("ChocoboBreedTraining", true, 0);
            SetState(FeedState.WaitingForCommenceConfirmation);
            return;
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 8)
            Fail("Commence window did not appear after selecting Feed.");
    }

    private void HandleWaitingForCommenceConfirmation()
    {
        if (GameHelpers.IsAddonVisible("SelectYesno"))
        {
            log.Information($"[ChokeAbo] Confirming training via {GameHelpers.FormatCallbackCommand("SelectYesno", true, 0)}");
            GameHelpers.FireAddonCallback("SelectYesno", true, 0);
            SetState(FeedState.WaitingForFeedResolution);
            return;
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 6)
            Fail("Training confirmation window did not appear after Commence.");
    }

    private void HandleWaitingForFeedResolution()
    {
        var current = CurrentEntry;
        if (current == null)
        {
            Complete();
            return;
        }

        var currentCount = inventoryService.GetItemCount(current.ItemId);
        if (currentCount < preFeedCount)
        {
            if (!GameHelpers.IsPlayerAvailable())
                return;

            remainingFeedsForCurrentEntry--;
            log.Information($"[ChokeAbo] Fed {current.FeedName}. Remaining for this feed: {remainingFeedsForCurrentEntry}");

            if (remainingFeedsForCurrentEntry > 0)
            {
                SetState(FeedState.InteractingTrainer);
                return;
            }

            currentEntryIndex++;
            if (CurrentEntry == null)
            {
                Complete();
                return;
            }

            remainingFeedsForCurrentEntry = CurrentEntry.PlannedQuantity;
            SetState(FeedState.InteractingTrainer);
            return;
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 20)
            Fail($"Feed did not appear to consume {current.FeedName}. Check the trainer flow and inventory state.");
    }

    private unsafe uint GetVisibleInventoryAddonId()
    {
        foreach (var addonName in InventoryAddonNames)
        {
            nint addonPtr = gameGui.GetAddonByName(addonName, 1);
            if (addonPtr == nint.Zero)
                continue;

            var addon = (AtkUnitBase*)addonPtr;
            if (addon->IsVisible)
                return addon->Id;
        }

        return 0;
    }

    private unsafe bool IsAnyInventoryAddonVisible()
    {
        foreach (var addonName in InventoryAddonNames)
        {
            nint addonPtr = gameGui.GetAddonByName(addonName, 1);
            if (addonPtr == nint.Zero)
                continue;

            var addon = (AtkUnitBase*)addonPtr;
            if (addon->IsVisible)
                return true;
        }

        return false;
    }

    private void Complete()
    {
        GameHelpers.StopMovement();
        SetState(FeedState.Complete);
    }

    private void SetState(FeedState newState)
    {
        state = newState;
        stateEnteredAtUtc = DateTime.UtcNow;
    }

    private void Fail(string message)
    {
        GameHelpers.StopMovement();
        lastError = message;
        log.Warning($"[ChokeAbo] {message}");
        SetState(FeedState.Failed);
    }
}
