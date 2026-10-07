using Dalamud.Plugin.Services;

namespace ChokeAbo.Services;

public sealed partial class BreedingService
{
    private readonly Configuration configuration;
    private readonly CharacterStateService characterStateService;
    private readonly InventoryService inventoryService;
    private readonly ChocoboStatsService chocoboStatsService;
    private readonly VendorPurchaseService vendorPurchaseService;
    private readonly StableFeedingService stableFeedingService;
    private readonly IPluginLog log;
    private readonly Func<bool> isOtherAutomationRunning;

    private ulong activeContentId;
    private bool ownsFeedServices;
    private bool feedActionStartedThisLoad;
    private string runtimeStatus = "Idle";
    private DateTime targetLeaseUntilUtc;
    private bool nativeUiStepActive;
    private DateTime nativeUiStepStarted;
    private DateTime nativeInteractionAt;
    private bool nativeContextOpened;
    private bool nativeContextSelected;
    private bool nativeNameSelectorOpened;
    private int nativeNameStage;
    private bool nativeTutorial;
    private bool nativeTutorialMenuSelected;
    private bool nativeTutorialCourseSelected;
    private bool nativeRetirementMenuSelected;
    private bool nativeRetirementFarewellSelected;
    private bool nativeRetirementRefreshRequested;
    private bool nativeSupplyMenuSelected;
    private bool nativeSupplyCloseRequested;
    private bool nativeBreederTravelStarted;
    private bool nativeCoveringMenuSelected;
    private int nativeCoveringSelectedParents;
    private bool nativeCoveringConfirmationSent;
    private bool nativeRegistrationMenuSelected;
    private bool raceAdmissionAllowed;
    internal Action? InspectNativeState { get; set; }
    internal Func<PopupCaptureKind, bool>? BeginNativeCapture { get; set; }
    internal Action? EndNativeCapture { get; set; }
    private bool ownsNativeCapture;
    private bool reconcileNativeUiAfterLoad = true;

    public BreedingService(
        Configuration configuration,
        CharacterStateService characterStateService,
        InventoryService inventoryService,
        ChocoboStatsService chocoboStatsService,
        VendorPurchaseService vendorPurchaseService,
        StableFeedingService stableFeedingService,
        IPluginLog log,
        Func<bool> isOtherAutomationRunning)
    {
        this.configuration = configuration;
        this.characterStateService = characterStateService;
        this.inventoryService = inventoryService;
        this.chocoboStatsService = chocoboStatsService;
        this.vendorPurchaseService = vendorPurchaseService;
        this.stableFeedingService = stableFeedingService;
        this.log = log;
        this.isOtherAutomationRunning = isOtherAutomationRunning;
    }

    public bool IsRunning => StockCleanupRunning || GameActionInProgress;

    public bool OwnsFeedServices => ownsFeedServices;
    public OffspringGoal CurrentOffspringGoal => characterStateService.GetCurrent().OffspringGoal;

    public bool GameActionInProgress
    {
        get
        {
            if (StockCleanupRunning) return true;
            var state = characterStateService.GetCurrent();
            return state.ExecutionOwner == BreedingExecutionOwner.Target &&
                   (nativeUiStepActive || state.Phase is BreedingPhase.Planning
                       or BreedingPhase.PurchasingFeed
                       or BreedingPhase.Feeding);
        }
    }

    public string StatusText
    {
        get
        {
            if (StockCleanupRunning) return StockCleanupStatus;
            var state = characterStateService.GetCurrent();
            return state.Phase switch
            {
                BreedingPhase.PurchasingFeed => $"Target feeding purchase: {vendorPurchaseService.StatusText}",
                BreedingPhase.Feeding => $"Target feeding: {stableFeedingService.StatusText}",
                BreedingPhase.Racing => state.BlockReason,
                BreedingPhase.CoveringWait => BuildCoveringWaitStatus(state),
                BreedingPhase.Paused => "Target cycle paused at a safe boundary.",
                BreedingPhase.Blocked => state.BlockReason,
                BreedingPhase.TargetReady => state.BlockReason,
                BreedingPhase.RetirementPendingCapture
                    or BreedingPhase.CoveringPendingCapture
                    or BreedingPhase.AdoptionPendingCapture
                    or BreedingPhase.RegistrationPendingCapture => state.BlockReason,
                BreedingPhase.Idle => runtimeStatus,
                _ => string.IsNullOrWhiteSpace(state.BlockReason) ? state.Phase.ToString() : state.BlockReason,
            };
        }
    }

    public bool StartOrResume()
    {
        if (StockCleanupRunning)
        {
            runtimeStatus = "Stock cleanup is active; stop it before starting breeding.";
            return false;
        }
        if (!TryGetCurrentIdentity(out _))
        {
            runtimeStatus = "Log into a character before starting breeding automation.";
            return false;
        }

        var state = characterStateService.GetCurrent();
        if (state.ExecutionOwner == BreedingExecutionOwner.Target)
        {
            runtimeStatus = "This target cycle is owned by VERMAXION; use its Target Pedigree controls.";
            return false;
        }

        characterStateService.TransitionCurrent(BreedingPhase.Blocked, current =>
        {
            current.ExecutionOwner = BreedingExecutionOwner.Manual;
            current.BlockReason = "Breeding UI mappings still require native inspection before automatic selection is available.";
        });
        runtimeStatus = characterStateService.GetCurrent().BlockReason;
        return false;
    }

    public TargetCycleStatus EnsureTargetCycle(TargetCycleEnsureRequest request)
    {
        if (!TryGetCurrentIdentity(out var contentId))
            return BuildProtocolBlock(0, "No logged-in Content ID is available.");
        if (request.ContentId != contentId)
            return BuildProtocolBlock(contentId, $"Content ID mismatch: request {request.ContentId}, current {contentId}.");
        if (!configuration.PluginEnabled)
            return BuildProtocolBlock(contentId, "Choke-abo is disabled.");
        if (StockCleanupRunning)
            return BuildProtocolBlock(contentId, "Stock cleanup is active; stop it before starting breeding.");

        activeContentId = contentId;
        characterStateService.MigrateLegacyStateIfNeeded(DateTime.UtcNow);
        var state = characterStateService.GetOrCreateCurrent();
        if (state.ProtocolVersion >= 3 && request.Version < 3)
            return BuildProtocolBlock(contentId, "This progression requires V3; older callers cannot remove its spending limits.");
        if (request.Version == 3 && state.Phase == BreedingPhase.Paused && !request.Resume)
            return GetTargetCycleStatus(contentId);
        if (request.StartNewBatch && (request.OffspringGoal == OffspringGoal.ReachPedigree || !request.Resume ||
            nativeUiStepActive || state.CollectionBaseline != null || state.ActionBaseline != null ||
            inventoryService.GetItemCount(ChocoboInventoryModel.ProofOfCoveringItemId) > 0 ||
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] ||
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InDutyQueue]))
            return BuildProtocolBlock(contentId, "Start a new offspring batch only after the existing action, queue and covering have settled.");
        targetLeaseUntilUtc = DateTime.UtcNow.AddSeconds(10);
        raceAdmissionAllowed = request.RaceAdmissionAllowed;
        var productionDefinitionChanged = state.OffspringGoal != request.OffspringGoal ||
            request.OffspringGoal == OffspringGoal.AbilityOffspring && state.DesiredInheritedAbilityId != request.DesiredInheritedAbilityId ||
            request.OffspringGoal == OffspringGoal.ColourOffspring && !state.AcceptableColourIds.SequenceEqual(request.AcceptableColourIds ?? Array.Empty<uint>());
        if (productionDefinitionChanged && inventoryService.GetItemCount(ChocoboInventoryModel.ProofOfCoveringItemId) > 0)
            return BuildProtocolBlock(contentId, "Finish the existing covering with its saved goal before changing the production target.");
        if (request.OffspringGoal == OffspringGoal.AbilityOffspring &&
            (!Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ChocoboRaceAbility>().TryGetRow(request.DesiredInheritedAbilityId, out var desiredAbility) ||
             string.IsNullOrWhiteSpace(desiredAbility.Name.ExtractText())))
            return BuildProtocolBlock(contentId, "The desired inherited ability is unavailable in current game data.");
        if (request.OffspringGoal == OffspringGoal.ColourOffspring && (request.AcceptableColourIds == null || request.AcceptableColourIds.Any(id =>
            !Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Stain>().TryGetRow(id, out var colour) || colour.IsMetallic || string.IsNullOrWhiteSpace(colour.Name.ExtractText()))))
            return BuildProtocolBlock(contentId, "The acceptable colours are unavailable in current game data.");
        var inputsChanged = state.ExecutionOwner == BreedingExecutionOwner.Target &&
                            (state.TargetPedigree != request.TargetPedigree ||
                             state.RetirementRank != request.RetirementRank ||
                             state.PreferredFeedGrade != request.PreferredFeedGrade ||
                             state.ProtocolVersion != request.Version ||
                             state.BreedingMode != request.BreedingMode ||
                             state.ProduceCounterpart != request.ProduceCounterpart ||
                             state.GilReserve != request.GilReserve || state.MgpReserve != request.MgpReserve ||
                             state.FeedPolicy != request.FeedPolicy || productionDefinitionChanged || request.StartNewBatch ||
                             request.OffspringGoal != OffspringGoal.ReachPedigree && RequestedOffspring(state) != request.RequestedOffspring);
        if (inputsChanged && (state.Phase is BreedingPhase.PurchasingFeed or BreedingPhase.Feeding || state.CollectionBaseline != null ||
            state.ActionBaseline != null && state.ActionRequestedAtUtc > state.ActionEvidenceAtUtc))
        {
            Block("Target inputs changed while an immediate feed action was awaiting evidence.");
            return GetTargetCycleStatus(contentId);
        }

        if (state.ExecutionOwner != BreedingExecutionOwner.Target || inputsChanged)
        {
            // Settings for the next pairing must not erase an already submitted covering.
            var retainCovering = state.ExecutionOwner == BreedingExecutionOwner.Target &&
                (state.Phase == BreedingPhase.CoveringWait ||
                 state.Phase == BreedingPhase.Paused && state.PhaseBeforePause == BreedingPhase.CoveringWait) &&
                state.CoveringEligibleAtUtc != DateTime.MinValue &&
                state.PrimarySelection != null && state.PartnerSelection != null &&
                inventoryService.GetItemCount(ChocoboInventoryModel.ProofOfCoveringItemId) == 1;
            StopOwnedFeedServices();
            characterStateService.TransitionCurrent(retainCovering ? BreedingPhase.CoveringWait : BreedingPhase.Planning, current =>
            {
                current.ExecutionOwner = BreedingExecutionOwner.Target;
                current.TargetPedigree = request.TargetPedigree;
                current.RetirementRank = request.RetirementRank;
                current.PreferredFeedGrade = request.PreferredFeedGrade;
                current.ProtocolVersion = request.Version;
                current.BreedingMode = request.BreedingMode;
                current.ProduceCounterpart = request.ProduceCounterpart;
                current.OffspringGoal = request.OffspringGoal;
                if (request.OffspringGoal == OffspringGoal.AbilityOffspring)
                {
                    if (current.DesiredInheritedAbilityId != request.DesiredInheritedAbilityId || request.StartNewBatch) current.AbilityOffspringProduced = 0;
                    current.DesiredInheritedAbilityId = request.DesiredInheritedAbilityId;
                    current.AbilityOffspringRequested = request.RequestedOffspring;
                }
                if (request.OffspringGoal == OffspringGoal.ColourOffspring)
                {
                    var colours = request.AcceptableColourIds ?? Array.Empty<uint>();
                    if (!current.AcceptableColourIds.SequenceEqual(colours) || request.StartNewBatch) current.ColourOffspringProduced = 0;
                    current.AcceptableColourIds = colours.ToList();
                    current.ColourOffspringRequested = request.RequestedOffspring;
                }
                current.GilReserve = request.GilReserve;
                current.MgpReserve = request.MgpReserve;
                current.FeedPolicy = request.FeedPolicy;
                current.SkippedFeedRank = 0;
                current.SkippedFeedPedigree = 0;
                if (!retainCovering)
                {
                    current.PrimarySelection = null;
                    current.PartnerSelection = null;
                    current.CoveringPurpose = CoveringPurpose.None;
                }
                current.ActionBaseline = null;
                current.FeedPurchaseEntries.Clear();
                current.PauseRequested = false;
                current.BlockReason = string.Empty;
            });
            feedActionStartedThisLoad = false;
        }
        else if (state.Phase == BreedingPhase.Paused && (request.Version == 2 || request.Resume))
        {
            characterStateService.TransitionCurrent(
                state.PhaseBeforePause is BreedingPhase.Idle or BreedingPhase.Paused
                    ? BreedingPhase.Planning
                    : state.PhaseBeforePause,
                current =>
                {
                    current.PauseRequested = false;
                    current.SkippedFeedRank = 0;
                    current.SkippedFeedPedigree = 0;
                    current.BlockReason = string.Empty;
                });
        }

        state = characterStateService.GetCurrent();
        if (request.Resume || !feedActionStartedThisLoad && state.Phase == BreedingPhase.Feeding)
        {
            ReconcileCancelledRegistration(state);
            ReconcileCancelledFeed(state);
            ReconcileCancelledRetirement(state);
            ReconcileCancelledCovering(state);
            ReconcileCancelledCollection(state);
        }
        if (request.Resume && state.Phase == BreedingPhase.Blocked && state.FeedPurchaseEntries.Count > 0 &&
            state.FeedPurchaseEntries.All(entry => inventoryService.GetItemCount(entry.ItemId) >= entry.OnHandQuantity + entry.QuantityToBuy))
            characterStateService.TransitionCurrent(BreedingPhase.PurchasingFeed, current => current.BlockReason = string.Empty);
        if (request.Resume && state.Phase == BreedingPhase.Blocked &&
            (state.ActionBaseline == null || state.ActionEvidenceAtUtc >= state.ActionRequestedAtUtc))
            characterStateService.TransitionCurrent(BreedingPhase.Planning, current => current.BlockReason = string.Empty);
        if (request.Resume && state.ActionBaseline?.ItemId is >= ChocoboInventoryModel.FirstRetiredItemId and <= ChocoboInventoryModel.LastRetiredItemId)
            characterStateService.TransitionCurrent(BreedingPhase.RetirementPendingCapture);
        if (request.Resume && state.RequiredPurchaseItemId != 0 && state.ActionBaseline?.ItemId == state.RequiredPurchaseItemId)
            characterStateService.TransitionCurrent(BreedingPhase.PurchasingSupplies);
        if (request.Resume && state.ActionBaseline?.ItemId == ChocoboInventoryModel.ProofOfCoveringItemId)
            characterStateService.TransitionCurrent(BreedingPhase.CoveringPendingCapture);
        if (request.Resume && state.CollectionBaseline != null)
            characterStateService.TransitionCurrent(BreedingPhase.AdoptionPendingCapture);
        if (state.Phase is BreedingPhase.PurchasingFeed or BreedingPhase.Feeding)
            return GetTargetCycleStatus(contentId);
        if (state.Phase == BreedingPhase.CoveringWait)
        {
            UpdateCoveringWait(state);
            return GetTargetCycleStatus(contentId);
        }
        if (state.Phase is BreedingPhase.RetirementPendingCapture
            or BreedingPhase.PurchasingSupplies
            or BreedingPhase.CoveringPendingCapture
            or BreedingPhase.AdoptionPendingCapture
            or BreedingPhase.RegistrationPendingCapture
            or BreedingPhase.Blocked)
        {
            if (!nativeUiStepActive && (state.Phase == BreedingPhase.AdoptionPendingCapture ||
                request.Resume && state.Phase is BreedingPhase.RetirementPendingCapture or BreedingPhase.RegistrationPendingCapture or BreedingPhase.PurchasingSupplies or BreedingPhase.CoveringPendingCapture))
                BeginNativeUiStep();
            return GetTargetCycleStatus(contentId);
        }

        AdvanceTargetCycle();
        return GetTargetCycleStatus(contentId);
    }

    public TargetCycleStatus PauseTargetCycle(ulong contentId)
    {
        if (!TryGetCurrentIdentity(out var currentContentId) || contentId != currentContentId)
            return BuildProtocolBlock(currentContentId, "Pause Content ID does not match the current character.");

        var state = characterStateService.GetCurrent();
        if (state.ExecutionOwner != BreedingExecutionOwner.Target)
            return GetTargetCycleStatus(contentId);

        CloseOwnedNativeInventory();
        characterStateService.SaveCurrent(current => current.PauseRequested = true);
        PauseAtSafeBoundaryIfRequested();
        return GetTargetCycleStatus(contentId);
    }

    public TargetCycleStatus SuspendTargetCycle(ulong contentId)
    {
        if (contentId != Plugin.PlayerState.ContentId) return BuildProtocolBlock(Plugin.PlayerState.ContentId, "Suspend identity mismatch.");
        CloseOwnedNativeInventory();
        targetLeaseUntilUtc = DateTime.MinValue;
        StopOwnedFeedServices();
        return GetTargetCycleStatus(contentId);
    }

    public TargetCycleStatus GetTargetCycleStatus(ulong contentId)
    {
        var currentContentId = Plugin.PlayerState.ContentId;
        if (contentId == 0 || contentId != currentContentId)
            return BuildProtocolBlock(currentContentId, "Status Content ID does not match the current character.");

        var state = characterStateService.GetCurrent();
        var targetReady = state.ExecutionOwner == BreedingExecutionOwner.Target && state.Phase == BreedingPhase.TargetReady &&
            state.OffspringGoal == OffspringGoal.ReachPedigree;
        var racer = chocoboStatsService.ReadActiveRacerSnapshot();
        var gameAction = GameActionInProgress;
        var shouldBlock = StockCleanupRunning || state.ExecutionOwner == BreedingExecutionOwner.Target && state.Phase switch
        {
            BreedingPhase.Racing => false,
            BreedingPhase.TargetReady => state.OffspringGoal != OffspringGoal.ReachPedigree,
            BreedingPhase.CoveringWait when state.CoveringPurpose == CoveringPurpose.ProduceMissingSex => !racer.IsLoaded,
            BreedingPhase.Idle => false,
            _ => true,
        };
        var reason = string.IsNullOrWhiteSpace(state.BlockReason) ? StatusText : state.BlockReason;
        return new TargetCycleStatus(
            TargetCycleProtocol.Version,
            contentId,
            TargetCycleProtocol.ToPhaseName(state.Phase),
            shouldBlock,
            targetReady,
            gameAction,
            reason,
            state.Phase == BreedingPhase.CoveringWait && state.CoveringEligibleAtUtc != DateTime.MinValue
                ? new DateTimeOffset(state.CoveringEligibleAtUtc.ToUniversalTime())
                : null,
            racer.IsLoaded ? racer.Pedigree : 0,
            racer.IsLoaded ? racer.Rank : 0,
            state.OffspringGoal == OffspringGoal.ReachPedigree && state.Phase == BreedingPhase.TargetReady && racer.IsLoaded &&
            racer.Pedigree == state.TargetPedigree && racer.Rank == 50,
            state.ExecutionOwner == BreedingExecutionOwner.Target && IsOwnedCoveringConfirmation(state, out _),
            racer.IsLoaded ? chocoboStatsService.Snapshot.InheritedAbilityId : 0,
            racer.IsLoaded ? chocoboStatsService.Snapshot.LearnedAbilityId : 0,
            racer.IsLoaded ? chocoboStatsService.Snapshot.ColourId : 0,
            chocoboStatsService.Snapshot.IsLoaded,
            state.OffspringGoal, ProducedOffspring(state), RequestedOffspring(state), state.OffspringCollected,
            state.OffspringGoal != OffspringGoal.ReachPedigree && state.Phase == BreedingPhase.TargetReady &&
            ProducedOffspring(state) >= RequestedOffspring(state));
    }

    public bool ShouldBlockRacing()
    {
        var racer = chocoboStatsService.ReadActiveRacerSnapshot();
        if (racer.IsLoaded)
            return false;

        var state = characterStateService.GetCurrent();
        if (state.ExecutionOwner == BreedingExecutionOwner.Target)
            return GetTargetCycleStatus(Plugin.PlayerState.ContentId).ShouldBlockRacing;

        return configuration.PluginEnabled && ChocoboInventoryModel.Enumerate(inventoryService)
            .Any(form => form.Kind == ChocoboFormKind.Retired && form.Capacity > 0);
    }

    public void Update()
    {
        if (StockCleanupRunning)
        {
            UpdateStockCleanup();
            return;
        }
        if (!TryGetCurrentIdentity(out var contentId))
            return;
        if (reconcileNativeUiAfterLoad && Plugin.PlayerState.IsLoaded &&
            !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] &&
            !Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
        {
            reconcileNativeUiAfterLoad = false;
            CloseOwnedNativeInventory();
            ReconcileInterruptedFeedTravel();
            if (characterStateService.GetCurrent().ExecutionOwner == BreedingExecutionOwner.Target) InspectNativeState?.Invoke();
        }

        if (activeContentId != 0 && activeContentId != contentId)
        {
            StopOwnedFeedServices();
            feedActionStartedThisLoad = false;
        }
        activeContentId = contentId;
        characterStateService.MigrateLegacyStateIfNeeded(DateTime.UtcNow);

        var state = characterStateService.GetCurrent();
        if (state.ExecutionOwner != BreedingExecutionOwner.Target)
            return;

        if (state.ProtocolVersion >= 3 && DateTime.UtcNow >= targetLeaseUntilUtc)
        {
            StopOwnedFeedServices();
            PauseAtSafeBoundaryIfRequested();
            return;
        }
        PauseAtSafeBoundaryIfRequested();
        state = characterStateService.GetCurrent();
        if (state.Phase == BreedingPhase.Paused)
            return;

        switch (state.Phase)
        {
            case BreedingPhase.RetirementPendingCapture:
            case BreedingPhase.PurchasingSupplies:
            case BreedingPhase.RegistrationPendingCapture:
            case BreedingPhase.CoveringPendingCapture:
            case BreedingPhase.AdoptionPendingCapture:
                if (nativeUiStepActive) UpdateNativeUiStep();
                break;
            case BreedingPhase.Planning:
                AdvanceTargetCycle();
                break;
            case BreedingPhase.PurchasingFeed:
                UpdateFeedPurchase(state);
                break;
            case BreedingPhase.Feeding:
                UpdateFeedAction(state);
                break;
            case BreedingPhase.CoveringWait:
                UpdateCoveringWait(state);
                break;
        }
    }

    public void Stop()
    {
        CancelStockCleanup();
        if (characterStateService.GetCurrent().ExecutionOwner == BreedingExecutionOwner.Target)
            PauseTargetCycle(Plugin.PlayerState.ContentId);
        else
            StopOwnedFeedServices();
    }

    private unsafe void AdvanceTargetCycle()
    {
        var state = characterStateService.GetCurrent();
        if (state.ExecutionOwner != BreedingExecutionOwner.Target || state.Phase == BreedingPhase.Paused)
            return;
        if (isOtherAutomationRunning())
        {
            Block("Another Choke-abo automation owns the feed/vendor services.");
            return;
        }

        var racer = chocoboStatsService.ReadActiveRacerSnapshot();
        if (!chocoboStatsService.Snapshot.IsLoaded)
        {
            if (chocoboStatsService.IsRefreshFailed)
            {
                Block($"Current racer data could not be loaded: {chocoboStatsService.StatusText}");
                return;
            }
            if (!chocoboStatsService.IsRefreshRunning)
                chocoboStatsService.RequestRefresh();
            TransitionStable(BreedingPhase.Planning, "Loading current racer data before selecting a breeding action.");
            return;
        }
        if (state.Phase == BreedingPhase.Planning && chocoboStatsService.IsRefreshComplete)
        {
            var info = Plugin.GameGui.GetAddonByName("GoldSaucerInfo");
            if (!info.IsNull && info.IsVisible)
                ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)info.Address)->Close(true);
        }
        if (state.ProtocolVersion >= 3 && racer.Pedigree == state.SkippedFeedPedigree && racer.Rank == state.SkippedFeedRank)
            racer = racer with { NeedsFeeding = false };
        var forms = ChocoboInventoryModel.Enumerate(inventoryService);
        if (state.ProtocolVersion >= 3 && racer.IsLoaded && !IsRacingTutorialComplete())
        {
            if (IsTutorialQueuedOrActive())
            {
                TransitionStable(BreedingPhase.Racing, "Initial racing tutorial admitted; VERMAXION owns the race.");
                return;
            }
            characterStateService.TransitionCurrent(BreedingPhase.RegistrationPendingCapture, current =>
            {
                current.PrimarySelection = null;
                current.BlockReason = "Complete the registrar's initial racing training course.";
            });
            BeginNativeUiStep();
            return;
        }
        var plan = state.ProtocolVersion >= 3
            ? PlanCurrentWorkflow(state, racer, forms)
            : TargetPedigreePlanner.Plan(state.TargetPedigree, state.RetirementRank, racer, forms);
        if (state.ProtocolVersion >= 3 && plan.Action == TargetPlanAction.RetireActive &&
            !FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(576))
        {
            Block("Complete Like Sire Like Fledgling before retiring this racer.");
            return;
        }
        switch (plan.Action)
        {
            case TargetPlanAction.Race:
                TransitionStable(BreedingPhase.Racing, plan.Reason);
                break;
            case TargetPlanAction.TargetReady:
                TransitionStable(BreedingPhase.TargetReady, plan.Reason);
                break;
            case TargetPlanAction.FeedActive:
                StartTargetFeedRound(state);
                break;
            case TargetPlanAction.RetireActive:
                TransitionCaptureBoundary(BreedingPhase.RetirementPendingCapture, plan, "retirement.md");
                break;
            case TargetPlanAction.BuyCoveringPermit:
            case TargetPlanAction.BuyRegistrationForm:
                characterStateService.TransitionCurrent(BreedingPhase.PurchasingSupplies, current =>
                {
                    current.RequiredPurchaseItemId = plan.RequiredItemId;
                    current.PrimarySelection = plan.Primary.HasValue ? SelectedItemEvidence.From(plan.Primary.Value) : null;
                    current.BlockReason = plan.Reason;
                });
                BeginNativeUiStep();
                break;
            case TargetPlanAction.RegisterFledgling:
                TransitionCaptureBoundary(BreedingPhase.RegistrationPendingCapture, plan, "fselector.md");
                break;
            case TargetPlanAction.CoverPair:
                TransitionCaptureBoundary(BreedingPhase.CoveringPendingCapture, plan, "cselector.md");
                break;
            case TargetPlanAction.WaitForCovering:
                Block("An unowned Proof of Covering is present; its exact start time cannot be inferred safely.");
                break;
            case TargetPlanAction.AdoptFledgling:
                TransitionCaptureBoundary(BreedingPhase.AdoptionPendingCapture, plan, "adoption.md");
                break;
            default:
                Block(plan.Reason);
                break;
        }
    }

    private static int ProducedOffspring(BreedingCharacterState state) => state.OffspringGoal switch
    {
        OffspringGoal.AbilityOffspring => state.AbilityOffspringProduced,
        OffspringGoal.ColourOffspring => state.ColourOffspringProduced,
        _ => 0,
    };

    private static int RequestedOffspring(BreedingCharacterState state) => state.OffspringGoal switch
    {
        OffspringGoal.AbilityOffspring => state.AbilityOffspringRequested,
        OffspringGoal.ColourOffspring => state.ColourOffspringRequested,
        _ => 0,
    };

    private static bool? MatchesProductionGoal(BreedingCharacterState state, ChocoboInventoryForm form)
        => state.OffspringGoal switch
        {
            OffspringGoal.AbilityOffspring => ChocoboInventoryModel.TryReadInheritedAbility(form, out var ability)
                ? ability == state.DesiredInheritedAbilityId : null,
            OffspringGoal.ColourOffspring => ChocoboInventoryModel.TryReadColour(form, out var colour)
                ? state.AcceptableColourIds.Contains(colour) : null,
            _ => null,
        };

    private TargetPedigreePlan PlanCurrentWorkflow(BreedingCharacterState state, ActiveRacerSnapshot racer,
        IReadOnlyList<ChocoboInventoryForm> forms)
    {
        if (state.OffspringGoal == OffspringGoal.ReachPedigree)
            return TargetPedigreePlanner.PlanProgression(state.TargetPedigree, racer, forms, state.BreedingMode, state.ProduceCounterpart);
        bool? racerMatches = state.OffspringGoal == OffspringGoal.AbilityOffspring
            ? chocoboStatsService.Snapshot.InheritedAbilityId == 0 ? null : chocoboStatsService.Snapshot.InheritedAbilityId == state.DesiredInheritedAbilityId
            : chocoboStatsService.Snapshot.ColourId == 0 ? null : state.AcceptableColourIds.Contains(chocoboStatsService.Snapshot.ColourId);
        return TargetPedigreePlanner.PlanOffspring(racer, forms, state.BreedingMode, ProducedOffspring(state), RequestedOffspring(state),
            form => MatchesProductionGoal(state, form), racerMatches);
    }

    private void StartTargetFeedRound(BreedingCharacterState state)
    {
        var stat = chocoboStatsService.SelectLowestEligibleStat();
        if (stat == null)
        {
            characterStateService.TransitionCurrent(BreedingPhase.Planning, current => current.BlockReason = string.Empty);
            AdvanceTargetCycle();
            return;
        }

        var single = chocoboStatsService.BuildSingleTargetFeedPlan(stat.Value, state.PreferredFeedGrade, state.ProtocolVersion < 3);
        var plan = state.ProtocolVersion >= 3 && single.Entries.Any(item => item.QuantityToBuy > 0)
            ? chocoboStatsService.BuildTargetFeedPurchasePlan(state.PreferredFeedGrade) : single;
        bool WithinReserves(FeedPurchasePlan candidate)
            => CanSpend(candidate.CurrentGil, candidate.TotalGil, state.GilReserve) &&
               CanSpend(candidate.CurrentMgp, candidate.TotalMgp, state.MgpReserve);
        string? UnavailableReason(FeedPurchasePlan candidate)
        {
            if (!WithinReserves(candidate)) return "Feed for the available training sessions cannot be purchased within the currency reserves.";
            if (candidate.Entries.Count(item => item.QuantityToBuy > 0 && item.OnHandQuantity == 0) > inventoryService.GetFreeMainInventorySlotCount())
                return "No free main-inventory slot is available for target feed.";
            return CanReachFeedServices(candidate.Entries.Any(item => item.QuantityToBuy > 0), out var reason) ? null : reason;
        }
        var unavailable = UnavailableReason(plan);
        if (state.ProtocolVersion >= 3 && unavailable != null &&
            state.FeedPolicy == InsufficientFeedPolicy.FallBack && state.PreferredFeedGrade > 1)
        {
            single = chocoboStatsService.BuildSingleTargetFeedPlan(stat.Value, 1, false);
            plan = single.Entries.Any(item => item.QuantityToBuy > 0)
                ? chocoboStatsService.BuildTargetFeedPurchasePlan(1) : single;
            unavailable = UnavailableReason(plan);
        }
        var entry = single.Entries.SingleOrDefault();
        if (entry == null || entry.ItemId == 0 || plan.Entries.Any(item => item.ItemId == 0))
        {
            Block("The selected target feed item could not be resolved.");
            return;
        }
        if (unavailable != null)
        {
            HandleUnavailableFeed(state, unavailable);
            return;
        }

        var selectedSlot = inventoryService.FindItemSlot(entry.ItemId);
        var baseline = new SelectedItemEvidence
        {
            ItemId = entry.ItemId,
            Container = selectedSlot.HasValue ? (int)selectedSlot.Value.container : 0,
            Slot = selectedSlot?.slot ?? -1,
            Quantity = inventoryService.GetItemCount(entry.ItemId),
        };
        if (entry.QuantityToBuy > 0)
        {
            characterStateService.TransitionCurrent(BreedingPhase.PurchasingFeed, current =>
            {
                current.ActionBaseline = baseline;
                current.ActionFeedGrade = entry.Grade;
                current.BaselineSessionsAvailable = chocoboStatsService.Snapshot.SessionsAvailable;
                current.ActionRequestedAtUtc = DateTime.UtcNow;
                current.ActionEvidenceAtUtc = DateTime.MinValue;
                current.FeedPurchaseEntries = state.ProtocolVersion >= 3 ? plan.Entries.Where(item => item.QuantityToBuy > 0).ToList() : new();
                current.BlockReason = $"Buying {plan.Entries.Sum(item => item.QuantityToBuy)} feed across {plan.Entries.Count(item => item.QuantityToBuy > 0)} items for {plan.PlannedTrainings} available training sessions.";
            });
            ownsFeedServices = true;
            feedActionStartedThisLoad = true;
            vendorPurchaseService.Reset();
            vendorPurchaseService.Start(plan, state.GilReserve, state.MgpReserve);
            return;
        }

        StartPersistedFeedAction(single, baseline);
    }

    private void StartPersistedFeedAction(FeedPurchasePlan plan, SelectedItemEvidence baseline)
    {
        var entry = plan.Entries.Single();
        characterStateService.TransitionCurrent(BreedingPhase.Feeding, current =>
        {
            current.ActionBaseline = baseline;
            current.BaselineSessionsAvailable = chocoboStatsService.Snapshot.SessionsAvailable;
            current.ActionRequestedAtUtc = DateTime.UtcNow;
            current.ActionEvidenceAtUtc = DateTime.MinValue;
            current.BlockReason = $"Feeding one {entry.FeedName}; awaiting exact item and session evidence.";
        });
        ownsFeedServices = true;
        feedActionStartedThisLoad = true;
        stableFeedingService.Reset();
        stableFeedingService.Start(plan, decrementConfiguredPlan: false);
    }

    private void UpdateFeedPurchase(BreedingCharacterState state)
    {
        if (state.FeedPurchaseEntries.Count > 0)
        {
            UpdateFeedPurchaseBatch(state);
            return;
        }
        var baseline = state.ActionBaseline;
        if (baseline == null)
        {
            Block("Persisted feed-purchase intent has no item baseline.");
            return;
        }

        var currentQuantity = inventoryService.GetItemCount(baseline.ItemId);
        if (vendorPurchaseService.IsComplete)
        {
            if (currentQuantity <= baseline.Quantity)
            {
                Block("Feed purchase completed without an exact item-ID quantity increase.");
                return;
            }

            vendorPurchaseService.Reset();
            characterStateService.SaveCurrent(current => current.ActionEvidenceAtUtc = DateTime.UtcNow);
            var stat = chocoboStatsService.SelectLowestEligibleStat();
            if (stat == null)
            {
                StopOwnedFeedServices();
                characterStateService.TransitionCurrent(BreedingPhase.Planning);
                return;
            }

            var plan = chocoboStatsService.BuildSingleTargetFeedPlan(stat.Value,
                state.ActionFeedGrade is >= 1 and <= 3 ? state.ActionFeedGrade : state.PreferredFeedGrade, false);
            if (plan.Entries.Count != 1 || plan.Entries[0].ItemId != baseline.ItemId)
            {
                Block("Feed selection changed after purchase; exact purchase evidence cannot be reconciled.");
                return;
            }

            var slot = inventoryService.FindItemSlot(baseline.ItemId);
            if (slot == null)
            {
                Block("Purchased feed could not be resolved to an exact inventory slot.");
                return;
            }

            StartPersistedFeedAction(plan, new SelectedItemEvidence
            {
                ItemId = baseline.ItemId,
                Container = (int)slot.Value.container,
                Slot = slot.Value.slot,
                Quantity = currentQuantity,
            });
            return;
        }

        if (vendorPurchaseService.IsFailed)
        {
            if (ReassessFeedReserveFailure()) return;
            if (!vendorPurchaseService.PurchaseWasDispatched)
                characterStateService.SaveCurrent(current =>
                {
                    current.ActionBaseline = null;
                    current.ActionEvidenceAtUtc = DateTime.UtcNow;
                });
            Block($"Target feed purchase blocked: {vendorPurchaseService.StatusText}");
            return;
        }

        if (!feedActionStartedThisLoad)
        {
            if (currentQuantity > baseline.Quantity)
            {
                characterStateService.TransitionCurrent(BreedingPhase.Planning, current => current.ActionEvidenceAtUtc = DateTime.UtcNow);
                AdvanceTargetCycle();
            }
            else
            {
                Block("Reload found an unverified feed-purchase intent; it will not be replayed.");
            }
        }
    }

    private void UpdateFeedPurchaseBatch(BreedingCharacterState state)
    {
        var fulfilled = state.FeedPurchaseEntries.All(entry =>
            inventoryService.GetItemCount(entry.ItemId) >= entry.OnHandQuantity + entry.QuantityToBuy);
        if (fulfilled)
        {
            log.Information($"[ChokeAbo][Target] Multi-buy verified for {state.FeedPurchaseEntries.Count} feed items; using the stocked feed before another vendor visit.");
            vendorPurchaseService.Reset();
            characterStateService.SaveCurrent(current =>
            {
                current.FeedPurchaseEntries.Clear();
                current.ActionBaseline = null;
                current.ActionEvidenceAtUtc = DateTime.UtcNow;
            });
            chocoboStatsService.ReadAvailableSnapshot();
            var stat = chocoboStatsService.SelectLowestEligibleStat();
            if (stat == null)
            {
                StopOwnedFeedServices();
                characterStateService.TransitionCurrent(BreedingPhase.Planning);
                return;
            }
            var single = chocoboStatsService.BuildSingleTargetFeedPlan(stat.Value, state.ActionFeedGrade, false);
            var entry = single.Entries.Single();
            var slot = inventoryService.FindItemSlot(entry.ItemId);
            if (slot == null) { Block("The completed feed batch does not contain the current training selection."); return; }
            StartPersistedFeedAction(single, new SelectedItemEvidence
            {
                ItemId = entry.ItemId, Container = (int)slot.Value.container, Slot = slot.Value.slot,
                Quantity = inventoryService.GetItemCount(entry.ItemId),
            });
            return;
        }
        if (vendorPurchaseService.IsFailed)
        {
            if (ReassessFeedReserveFailure()) return;
            if (!vendorPurchaseService.PurchaseWasDispatched)
                characterStateService.SaveCurrent(current =>
                {
                    current.FeedPurchaseEntries.Clear();
                    current.ActionBaseline = null;
                    current.ActionEvidenceAtUtc = DateTime.UtcNow;
                });
            Block($"Target feed purchase blocked: {vendorPurchaseService.StatusText}");
        }
        else if (!feedActionStartedThisLoad || vendorPurchaseService.IsComplete)
            Block("The feed batch is not fully present in inventory; reconcile its pending purchases before another vendor visit.");
    }

    private bool ReassessFeedReserveFailure()
    {
        if (!vendorPurchaseService.ReserveBlocked || vendorPurchaseService.PurchaseWasDispatched) return false;
        // No request was sent for the blocked item. Reuse any partial stock and
        // apply Fall back / Skip / Stop to the remaining plan using fresh balances.
        StopOwnedFeedServices();
        characterStateService.TransitionCurrent(BreedingPhase.Planning, current =>
        {
            current.FeedPurchaseEntries.Clear();
            current.ActionBaseline = null;
            current.ActionEvidenceAtUtc = DateTime.UtcNow;
        });
        StartTargetFeedRound(characterStateService.GetCurrent());
        return true;
    }

    private void UpdateFeedAction(BreedingCharacterState state)
    {
        var baseline = state.ActionBaseline;
        if (baseline == null)
        {
            Block("Persisted feeding intent has no item baseline.");
            return;
        }

        if (stableFeedingService.IsFailed)
        {
            Block($"Target feeding blocked: {stableFeedingService.StatusText}");
            return;
        }

        var itemCountDecreased = inventoryService.GetItemCount(baseline.ItemId) < baseline.Quantity;
        if (stableFeedingService.IsComplete)
        {
            chocoboStatsService.ReadAvailableSnapshot();
            var sessionsDecreased = chocoboStatsService.Snapshot.IsLoaded &&
                                    chocoboStatsService.Snapshot.SessionsAvailable < state.BaselineSessionsAvailable;
            if (!itemCountDecreased || !sessionsDecreased || stableFeedingService.ConfirmedFeedCount != 1)
            {
                Block("Feeding did not produce one exact feed-item decrease and one confirmed-session decrease.");
                return;
            }

            stableFeedingService.Reset();
            ownsFeedServices = false;
            feedActionStartedThisLoad = false;
            characterStateService.TransitionCurrent(BreedingPhase.Planning, current =>
            {
                current.ActionEvidenceAtUtc = DateTime.UtcNow;
                current.BlockReason = string.Empty;
            });
            PauseAtSafeBoundaryIfRequested();
            if (characterStateService.GetCurrent().Phase != BreedingPhase.Paused)
                AdvanceTargetCycle();
            return;
        }

        if (!feedActionStartedThisLoad)
        {
            chocoboStatsService.ReadAvailableSnapshot();
            var sessionsDecreased = chocoboStatsService.Snapshot.IsLoaded &&
                                    chocoboStatsService.Snapshot.SessionsAvailable < state.BaselineSessionsAvailable;
            if (itemCountDecreased && sessionsDecreased)
            {
                characterStateService.TransitionCurrent(BreedingPhase.Planning, current => current.ActionEvidenceAtUtc = DateTime.UtcNow);
                AdvanceTargetCycle();
            }
            else
            {
                Block("Reload found an unverified feeding intent; it will not be replayed.");
            }
        }
    }

    private void UpdateCoveringWait(BreedingCharacterState state)
    {
        if (state.CoveringEligibleAtUtc == DateTime.MinValue || DateTime.UtcNow < state.CoveringEligibleAtUtc)
            return;

        characterStateService.TransitionCurrent(BreedingPhase.AdoptionPendingCapture, current =>
        {
            current.BlockReason = "Covering is eligible; collection requires a verified native UI mapping.";
        });
        BeginNativeUiStep();
    }

    private void PauseAtSafeBoundaryIfRequested()
    {
        var state = characterStateService.GetCurrent();
        if (!state.PauseRequested || state.Phase == BreedingPhase.Paused)
            return;
        if (state.Phase is BreedingPhase.PurchasingFeed or BreedingPhase.Feeding &&
            (vendorPurchaseService.IsRunning || stableFeedingService.IsRunning))
        {
            return;
        }

        StopOwnedFeedServices();
        characterStateService.TransitionCurrent(BreedingPhase.Paused, current =>
        {
            current.PhaseBeforePause = state.Phase;
            current.PauseRequested = false;
            current.BlockReason = "Paused by VERMAXION at a safe boundary.";
        });
    }

    private void TransitionCaptureBoundary(BreedingPhase phase, TargetPedigreePlan plan, string captureFile)
    {
        characterStateService.TransitionCurrent(phase, current =>
        {
            current.PrimarySelection = plan.Primary.HasValue ? SelectedItemEvidence.From(plan.Primary.Value) : null;
            current.PartnerSelection = plan.Partner.HasValue ? SelectedItemEvidence.From(plan.Partner.Value) : null;
            current.CoveringPurpose = plan.CoveringPurpose;
            current.BlockReason = $"{plan.Reason} Native UI selection remains unverified (capture reference: {captureFile}).";
        });
        if (phase is BreedingPhase.RetirementPendingCapture or BreedingPhase.RegistrationPendingCapture or BreedingPhase.CoveringPendingCapture)
            BeginNativeUiStep();
    }

    private void BeginNativeUiStep()
    {
        nativeTutorial = !IsRacingTutorialComplete() && chocoboStatsService.ReadActiveRacerSnapshot().IsLoaded;
        nativeTutorialMenuSelected = false;
        nativeTutorialCourseSelected = false;
        nativeRetirementMenuSelected = false;
        nativeRetirementFarewellSelected = false;
        nativeRetirementRefreshRequested = false;
        nativeSupplyMenuSelected = false;
        nativeSupplyCloseRequested = false;
        nativeBreederTravelStarted = false;
        nativeCoveringMenuSelected = false;
        nativeCoveringSelectedParents = 0;
        nativeCoveringConfirmationSent = false;
        nativeRegistrationMenuSelected = false;
        if (!ownsNativeCapture)
            ownsNativeCapture = BeginNativeCapture?.Invoke(characterStateService.GetCurrent().Phase switch
            {
                BreedingPhase.RetirementPendingCapture => PopupCaptureKind.Retirement,
                BreedingPhase.PurchasingSupplies => PopupCaptureKind.CoveringSelector,
                BreedingPhase.CoveringPendingCapture => PopupCaptureKind.CoveringSelector,
                BreedingPhase.AdoptionPendingCapture => PopupCaptureKind.Adoption,
                _ => PopupCaptureKind.FledglingSelector,
            }) == true;
        nativeUiStepActive = true;
        nativeUiStepStarted = DateTime.UtcNow;
        nativeInteractionAt = DateTime.MinValue;
        nativeContextOpened = false;
        nativeContextSelected = false;
        nativeNameSelectorOpened = false;
        nativeNameStage = 0;
    }

    private static unsafe bool IsRacingTutorialComplete()
    {
        var player = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return player != null && (player->GoldSaucerContentStatus & 1) != 0;
    }

    private static unsafe bool IsTutorialQueuedOrActive()
    {
        if (Plugin.ClientState.TerritoryType == 417 &&
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty]) return true;
        var finder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder.Instance();
        if (finder == null) return false;
        if (finder->QueueInfo.PoppedQueueEntry.Id == 481) return true;
        foreach (var entry in finder->QueueInfo.QueuedEntries)
            if (entry.Id == 481) return true;
        return false;
    }

    private unsafe void ReconcileCancelledFeed(BreedingCharacterState state)
    {
        if (state.Phase is not (BreedingPhase.Feeding or BreedingPhase.Blocked) || state.ActionBaseline is not { } feed ||
            !GameHelpers.IsPlayerAvailable() || GameHelpers.IsAddonVisible("ChocoboBreedTraining") ||
            inventoryService.GetItemCount(feed.ItemId) != feed.Quantity) return;
        if (!IsKnownFeedItem(feed.ItemId)) return;
        var inventory = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventory.Instance();
        if (inventory == null || inventory->CurrentInventoryContextEvent != null) return;
        chocoboStatsService.ReadAvailableSnapshot();
        if (!chocoboStatsService.Snapshot.IsLoaded ||
            chocoboStatsService.Snapshot.SessionsAvailable != state.BaselineSessionsAvailable) return;
        StopOwnedFeedServices();
        characterStateService.TransitionCurrent(BreedingPhase.Planning, current =>
        {
            current.ActionBaseline = null;
            current.ActionEvidenceAtUtc = DateTime.UtcNow;
        });
        log.Information("[ChokeAbo][Native] Interrupted feeding reconciled: feed and training allowance unchanged, session released.");
    }

    private bool IsKnownFeedItem(uint itemId)
        => itemId != 0 && Enumerable.Range(1, 3).SelectMany(grade => Enum.GetValues<ChocoboStatKind>()
                .Select(stat => FeedCatalog.Get(stat, grade)))
            .Any(definition => inventoryService.ResolveItemId(definition.FeedName) == itemId);

    private unsafe void ReconcileCancelledRegistration(BreedingCharacterState state)
    {
        if (state.ActionBaseline is not { } form ||
            form.ItemId is < ChocoboInventoryModel.FirstFledglingItemId or > ChocoboInventoryModel.LastFledglingItemId ||
            !GameHelpers.IsPlayerAvailable() || GameHelpers.IsAddonVisible("ChocoboBreedNaming") ||
            chocoboStatsService.ReadActiveRacerSnapshot().IsLoaded) return;
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventory.Instance();
        if (agent == null || agent->CurrentInventoryContextEvent != null) return;
        if (!ChocoboInventoryModel.Enumerate(inventoryService).Any(item => item.ItemId == form.ItemId &&
            (int)item.Container == form.Container && item.Slot == form.Slot && item.Quantity == form.Quantity)) return;
        characterStateService.SaveCurrent(current =>
        {
            current.ActionBaseline = null;
            current.ActionEvidenceAtUtc = DateTime.UtcNow;
        });
        log.Information("[ChokeAbo][Native] Cancelled registration reconciled: original form retained, no racer, and inventory session released.");
    }

    private unsafe void ReconcileCancelledCovering(BreedingCharacterState state)
    {
        if (state.ActionBaseline?.ItemId != ChocoboInventoryModel.ProofOfCoveringItemId ||
            state.PrimarySelection is not { } primary || state.PartnerSelection is not { } partner ||
            !GameHelpers.IsPlayerAvailable() || GameHelpers.IsAddonVisible("ChocoboBreedCoupling") ||
            GameHelpers.IsAddonVisible("SelectYesno") ||
            inventoryService.GetItemCount(ChocoboInventoryModel.ProofOfCoveringItemId) != state.ActionBaseline.Quantity) return;
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventory.Instance();
        if (agent == null || agent->CurrentInventoryContextEvent != null) return;
        bool Unchanged(SelectedItemEvidence item) => inventoryService.FindItem(item.InventoryType, item.Slot) is { } live &&
            live.ItemId == item.ItemId && live.Condition == item.Condition && live.Quantity == item.Quantity;
        if (!Unchanged(primary) || !Unchanged(partner)) return;
        characterStateService.SaveCurrent(current => { current.ActionBaseline = null; current.ActionEvidenceAtUtc = DateTime.UtcNow; });
        log.Information("[ChokeAbo][Native] Cancelled covering reconciled: both original stock items/capacities unchanged, no proof, and native session released.");
    }

    private void ReconcileCancelledRetirement(BreedingCharacterState state)
    {
        if (!GameHelpers.IsPlayerAvailable() ||
            state.ActionBaseline is not { ItemId: >= ChocoboInventoryModel.FirstRetiredItemId and <= ChocoboInventoryModel.LastRetiredItemId } retirement ||
            inventoryService.GetItemCount(retirement.ItemId) != retirement.Quantity) return;
        var racer = chocoboStatsService.ReadActiveRacerSnapshot();
        var first = racer.Sex == ChocoboSex.Female ? ChocoboInventoryModel.FirstFemaleRetiredItemId : ChocoboInventoryModel.FirstRetiredItemId;
        if (!racer.IsLoaded || racer.Rank < 40 || racer.Sex == ChocoboSex.Unknown || first + racer.Pedigree - 1 != retirement.ItemId) return;
        characterStateService.TransitionCurrent(BreedingPhase.Planning, current =>
        {
            current.ActionBaseline = null;
            current.ActionEvidenceAtUtc = DateTime.UtcNow;
            current.BlockReason = string.Empty;
        });
        log.Information("[ChokeAbo][Native] Cancelled retirement reconciled: same rank40 racer retained, retired-form quantity unchanged, interaction released.");
    }

    private unsafe void UpdateNativeUiStep()
    {
        if (DateTime.UtcNow - nativeUiStepStarted > TimeSpan.FromMinutes(2))
        {
            nativeUiStepActive = false;
            InspectNativeState?.Invoke();
            var pending = characterStateService.GetCurrent();
            Block(pending.RequiredPurchaseItemId != 0 && pending.ActionBaseline is { } purchase &&
                  purchase.ItemId == pending.RequiredPurchaseItemId && inventoryService.GetItemCount(purchase.ItemId) > purchase.Quantity
                ? "Breeding supply is purchased, but the supplier interaction did not release; Resume to reconcile the retained item."
                : "Could not reach the required breeding service for native UI inspection.");
            return;
        }
        var current = characterStateService.GetCurrent();
        var buyingG1 = current.Phase == BreedingPhase.PurchasingSupplies &&
            current.RequiredPurchaseItemId is ChocoboInventoryModel.FirstFledglingItemId or ChocoboInventoryModel.FirstFemaleFledglingItemId;
        if (current.Phase == BreedingPhase.PurchasingSupplies && current.RequiredPurchaseItemId != 0 &&
            current.ActionBaseline?.ItemId == current.RequiredPurchaseItemId)
        {
            UpdateRequiredSupplyPurchase(current);
            return;
        }
        if (buyingG1 && Plugin.TargetManager.Target?.BaseId == 1011585 &&
            (GameHelpers.IsAddonVisible("Shop") || GameHelpers.IsAddonVisible("SelectIconString") || GameHelpers.IsAddonVisible("SelectString")) &&
            DateTime.UtcNow - nativeInteractionAt > TimeSpan.FromSeconds(2))
        {
            StartRequiredSupplyPurchase(current);
            return;
        }
        if (current.Phase == BreedingPhase.AdoptionPendingCapture && UpdateAdoption(current)) return;
        if (current.Phase == BreedingPhase.RegistrationPendingCapture && !nativeRegistrationMenuSelected &&
            current.PrimarySelection != null && Plugin.TargetManager.Target?.BaseId == 1010465 &&
            GameHelpers.TrySelectNativeListEntry("SelectIconString", "Race Chocobo Registration", () => { }, observedNodeId: 3))
        {
            nativeRegistrationMenuSelected = true;
            nativeInteractionAt = DateTime.UtcNow;
            return;
        }
        if (current.Phase == BreedingPhase.CoveringPendingCapture &&
            current.ActionBaseline is { ItemId: ChocoboInventoryModel.ProofOfCoveringItemId } proof)
        {
            if (inventoryService.GetItemCount(proof.ItemId) > proof.Quantity)
            {
                var readyAt = current.ActionRequestedAtUtc.AddHours(24);
                log.Information($"[ChokeAbo][Native] Covering verified: Proof of Covering increased from {proof.Quantity}; eligible {readyAt:O}.");
                InspectNativeState?.Invoke();
                StopOwnedFeedServices();
                characterStateService.TransitionCurrent(BreedingPhase.CoveringWait, state =>
                {
                    state.ActionEvidenceAtUtc = DateTime.UtcNow;
                    state.ActionBaseline = null;
                    state.CoveringEligibleAtUtc = readyAt;
                    state.CoveringWaitMigratedTo24Hours = true;
                    state.BlockReason = string.Empty;
                });
                return;
            }
            if (!nativeCoveringConfirmationSent && IsOwnedCoveringConfirmation(current, out var coveringFee))
            {
                if (!CanSpend(inventoryService.GetItemCount(1), coveringFee, current.GilReserve))
                {
                    StopOwnedFeedServices();
                    characterStateService.TransitionCurrent(BreedingPhase.Paused, state =>
                    {
                        state.PhaseBeforePause = BreedingPhase.CoveringPendingCapture;
                        state.BlockReason = "The covering confirmation would cross the gil reserve. Explicit Resume is required.";
                    });
                    return;
                }
                characterStateService.SaveCurrent(state => state.ActionRequestedAtUtc = DateTime.UtcNow);
                nativeCoveringConfirmationSent = GameHelpers.TryClickNativeButton("SelectYesno", "Yes", 8);
                if (nativeCoveringConfirmationSent) return;
            }
            if (DateTime.UtcNow - current.ActionRequestedAtUtc < TimeSpan.FromSeconds(3)) return;
            InspectNativeState?.Invoke();
            Block("Covering Commence was dispatched; its exact confirmation or proof result must be reconciled before another covering.");
            return;
        }
        if (current.Phase == BreedingPhase.CoveringPendingCapture && GameHelpers.IsAddonVisible("ChocoboBreedCoupling") &&
            DateTime.UtcNow - nativeInteractionAt > TimeSpan.FromSeconds(2))
        {
            var parentInventory = Plugin.GameGui.GetAddonByName("InventoryExpansion");
            if (parentInventory.IsNull || !parentInventory.IsVisible) parentInventory = Plugin.GameGui.GetAddonByName("InventoryLarge");
            var parent = nativeCoveringSelectedParents == 0 ? current.PrimarySelection : current.PartnerSelection;
            if (nativeCoveringSelectedParents < 2 && !nativeContextOpened && parent != null && !parentInventory.IsNull && parentInventory.IsVisible)
            {
                if (!ChocoboInventoryModel.Enumerate(inventoryService).Any(item => item.ItemId == parent.ItemId &&
                    (int)item.Container == parent.Container && item.Slot == parent.Slot && item.Capacity == parent.Condition))
                { Block("The retained covering parent changed before native selection."); return; }
                FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance()
                    ->OpenForItemSlot(parent.InventoryType, parent.Slot, 0, ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)parentInventory.Address)->Id);
                nativeContextOpened = true;
                nativeInteractionAt = DateTime.UtcNow;
                return;
            }
            var context = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance();
            if (nativeCoveringSelectedParents < 2 && nativeContextOpened && parent != null && context != null &&
                context->TargetInventoryId == parent.InventoryType && context->TargetInventorySlotId == parent.Slot &&
                GameHelpers.IsAddonVisible("ContextMenu") &&
                GameHelpers.TrySelectNativeListEntry("ContextMenu", "Select Stock", () => { }, observedNodeId: 2))
            {
                ++nativeCoveringSelectedParents;
                nativeContextOpened = false;
                nativeInteractionAt = DateTime.UtcNow;
                return;
            }
            if (nativeCoveringSelectedParents == 2 && TryCommenceCovering(current)) return;
            InspectNativeState?.Invoke();
            Block("The selected covering stock is captured; native selected-parent evidence and the fee require verification before Commence.");
            return;
        }
        if (nativeTutorial && IsRacingTutorialComplete())
        {
            if (nativeTutorialMenuSelected)
            {
                var finder = Plugin.GameGui.GetAddonByName("ContentsFinder");
                if (!finder.IsNull && finder.IsVisible)
                    ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)finder.Address)->Close(true);
            }
            StopOwnedFeedServices();
            nativeTutorial = false;
            TransitionStable(BreedingPhase.Planning, "Native racing unlock verified; reassessing feeding and racing.");
            return;
        }
        if (nativeTutorial && nativeTutorialMenuSelected)
        {
            if (IsTutorialQueuedOrActive())
            {
                nativeUiStepActive = false;
                if (ownsNativeCapture) { ownsNativeCapture = false; EndNativeCapture?.Invoke(); }
                TransitionStable(BreedingPhase.Racing, "Initial racing tutorial admitted; VERMAXION owns the race.");
                return;
            }
            if (!raceAdmissionAllowed)
            {
                StopOwnedFeedServices();
                TransitionStable(BreedingPhase.Racing, "The initial racing tutorial awaits the next daily allowance.");
                return;
            }
            if (DateTime.UtcNow - nativeInteractionAt < TimeSpan.FromSeconds(2)) return;
            if (!nativeTutorialCourseSelected && GameHelpers.TrySelectNativeListEntry("SelectString", "Training Course.", () => { }))
            {
                nativeTutorialCourseSelected = true;
                nativeInteractionAt = DateTime.UtcNow;
                return;
            }
            InspectNativeState?.Invoke();
            Block("Registrar race entry captured; initial training admission is awaiting verification.");
            return;
        }
        if (nativeTutorial && Plugin.TargetManager.Target?.BaseId == 1010464 &&
            GameHelpers.TrySelectNativeListEntry("SelectString", "Enter a race.", () => { }))
        {
            nativeTutorialMenuSelected = true;
            nativeInteractionAt = DateTime.UtcNow;
            return;
        }
        if (current.Phase == BreedingPhase.PurchasingSupplies && Plugin.TargetManager.Target?.BaseId == 1010488)
        {
            if (nativeSupplyMenuSelected || GameHelpers.IsAddonVisible("ShopExchangeCurrency"))
            {
                if (DateTime.UtcNow - nativeInteractionAt < TimeSpan.FromSeconds(2)) return;
                StartRequiredSupplyPurchase(current);
                return;
            }
            if (GameHelpers.TrySelectNativeListEntry("SelectIconString", "Race Items", () => { }))
            {
                nativeSupplyMenuSelected = true;
                nativeInteractionAt = DateTime.UtcNow;
                return;
            }
        }
        if (current.Phase == BreedingPhase.CoveringPendingCapture && !nativeCoveringMenuSelected &&
            Plugin.TargetManager.Target?.BaseId == 1010472 &&
            GameHelpers.TrySelectNativeListEntry("SelectIconString", "Chocobo Covering", () => { }, observedNodeId: 3))
        {
            nativeCoveringMenuSelected = true;
            nativeInteractionAt = DateTime.UtcNow;
            return;
        }
        if (current.Phase == BreedingPhase.RetirementPendingCapture)
        {
            if (current.ActionBaseline is { ItemId: >= ChocoboInventoryModel.FirstRetiredItemId and <= ChocoboInventoryModel.LastRetiredItemId } retirement)
            {
                chocoboStatsService.ReadActiveRacerSnapshot();
                if (chocoboStatsService.Snapshot.IsLoaded && chocoboStatsService.Snapshot.Rank == 0 &&
                    inventoryService.GetItemCount(retirement.ItemId) > retirement.Quantity)
                {
                    log.Information($"[ChokeAbo][Native] Retirement verified: registered racer cleared and retired form {retirement.ItemId} increased.");
                    InspectNativeState?.Invoke();
                    StopOwnedFeedServices();
                    characterStateService.TransitionCurrent(BreedingPhase.Planning, state =>
                    {
                        state.ActionBaseline = null;
                        state.ActionEvidenceAtUtc = DateTime.UtcNow;
                        state.BlockReason = string.Empty;
                    });
                    return;
                }
                if (DateTime.UtcNow - nativeInteractionAt < TimeSpan.FromSeconds(3)) return;
                if (!nativeRetirementFarewellSelected && Plugin.TargetManager.Target?.BaseId == 1010465 &&
                    GameHelpers.TrySelectNativeListEntry("SelectString", null,
                        () => characterStateService.SaveCurrent(state => state.ActionRequestedAtUtc = DateTime.UtcNow),
                        matches: text => text.StartsWith("Fare thee well, old friend.", StringComparison.Ordinal) &&
                            text.EndsWith(", and how it carried us to victory!", StringComparison.Ordinal)))
                {
                    nativeRetirementFarewellSelected = true;
                    nativeInteractionAt = DateTime.UtcNow;
                    return;
                }
                if (nativeRetirementFarewellSelected && GameHelpers.IsPlayerAvailable() &&
                    DateTime.UtcNow - nativeInteractionAt < TimeSpan.FromSeconds(30))
                {
                    if (!nativeRetirementRefreshRequested)
                    {
                        nativeRetirementRefreshRequested = true;
                        chocoboStatsService.RequestRefresh();
                    }
                    return;
                }
                InspectNativeState?.Invoke();
                Block("Retirement confirmation sent; awaiting the retired form and cleared current racer. Any further native prompt is captured before another action.");
                return;
            }
            if (IsRetirementConfirmationVisible())
            {
                var racer = chocoboStatsService.ReadActiveRacerSnapshot();
                if (!racer.IsLoaded || racer.Rank < 40 || racer.Pedigree >= current.TargetPedigree ||
                    racer.Sex == ChocoboSex.Unknown || inventoryService.GetFreeMainInventorySlotCount() == 0)
                { Block("Retirement requires an intermediate rank40 racer and room for its retained form."); return; }
                var retiredItem = (racer.Sex == ChocoboSex.Female ? ChocoboInventoryModel.FirstFemaleRetiredItemId : ChocoboInventoryModel.FirstRetiredItemId)
                    + (uint)racer.Pedigree - 1;
                characterStateService.SaveCurrent(state =>
                {
                    state.ActionBaseline = new SelectedItemEvidence { ItemId = retiredItem, Quantity = inventoryService.GetItemCount(retiredItem) };
                    state.ActionRequestedAtUtc = DateTime.UtcNow;
                    state.ActionEvidenceAtUtc = DateTime.MinValue;
                });
                if (!GameHelpers.TryClickNativeButton("SelectYesno", "Yes", 8))
                {
                    characterStateService.SaveCurrent(state => { state.ActionBaseline = null; state.ActionEvidenceAtUtc = DateTime.UtcNow; });
                    Block("The observed retirement confirmation button is unavailable.");
                    return;
                }
                nativeInteractionAt = DateTime.UtcNow;
                return;
            }
            if (nativeRetirementMenuSelected)
            {
                if (DateTime.UtcNow - nativeInteractionAt < TimeSpan.FromSeconds(2)) return;
                InspectNativeState?.Invoke();
                Block("Retirement menu selected; the resulting native window is captured before confirming retirement.");
                return;
            }
            if (Plugin.TargetManager.Target?.BaseId == 1010465 &&
                GameHelpers.TrySelectNativeListEntry("SelectIconString", "Retiring a Race Chocobo", () => { }))
            {
                nativeRetirementMenuSelected = true;
                nativeInteractionAt = DateTime.UtcNow;
                return;
            }
        }
        if (nativeNameSelectorOpened)
        {
            if (DateTime.UtcNow - nativeInteractionAt < TimeSpan.FromSeconds(1)) return;
            if (nativeNameStage >= 7)
            {
                var racer = chocoboStatsService.ReadActiveRacerSnapshot();
                var registrationForm = current.ActionBaseline;
                var remainingForm = registrationForm == null ? null : inventoryService.FindItem(registrationForm.InventoryType, registrationForm.Slot);
                if (registrationForm != null && racer.IsLoaded &&
                    (remainingForm == null || remainingForm.Value.ItemId != registrationForm.ItemId || remainingForm.Value.Quantity < registrationForm.Quantity) &&
                    ChocoboInventoryForm.TryDecode(new InventoryItemSnapshot(registrationForm.ItemId, string.Empty, registrationForm.Condition,
                        registrationForm.Quantity, registrationForm.InventoryType, registrationForm.Slot), out var selected) &&
                    racer.Pedigree == selected.Pedigree && racer.Sex == selected.Sex)
                {
                    log.Information($"[ChokeAbo][Native] Registration verified: form {registrationForm.ItemId} consumed; pedigree={racer.Pedigree}; racingRank={racer.Rank}; sex={racer.Sex}.");
                    InspectNativeState?.Invoke();
                    StopOwnedFeedServices();
                    characterStateService.TransitionCurrent(BreedingPhase.Planning, state =>
                    {
                        state.ActionEvidenceAtUtc = DateTime.UtcNow;
                        state.ActionBaseline = null;
                        state.BlockReason = string.Empty;
                    });
                    return;
                }
                if (nativeNameStage == 7 && DateTime.UtcNow - nativeInteractionAt >= TimeSpan.FromSeconds(3))
                { InspectNativeState?.Invoke(); nativeNameStage = 8; }
                if (GameHelpers.IsPlayerAvailable() && !chocoboStatsService.IsRefreshRunning && !racer.IsLoaded)
                    chocoboStatsService.RequestRefresh();
                if (DateTime.UtcNow - nativeInteractionAt > TimeSpan.FromSeconds(30))
                    Block("Registration confirmation was sent but matching form consumption and current racer evidence are unavailable.");
                return;
            }
            var acted = nativeNameStage switch
            {
                0 or 3 => GameHelpers.TrySelectNativeListEntry("ChocoboBreedNamingList", null, () => { }, randomize: true, observedNodeId: 8),
                1 or 4 => GameHelpers.TryClickNativeButton("ChocoboBreedNamingList", "OK"),
                2 => !GameHelpers.IsAddonVisible("ChocoboBreedNamingList") &&
                    GameHelpers.TryClickNativeButton("ChocoboBreedNaming", null, 8),
                5 => !GameHelpers.IsAddonVisible("ChocoboBreedNamingList") &&
                    GameHelpers.TryClickNativeButton("ChocoboBreedNaming", "OK"),
                6 => TryConfirmRegistration(current),
                _ => false,
            };
            if (acted)
            {
                ++nativeNameStage;
                nativeInteractionAt = DateTime.UtcNow;
                return;
            }
            InspectNativeState?.Invoke();
            Block($"Registration name stage {nativeNameStage} captured; final registration evidence remains pending.");
            return;
        }
        if (nativeContextSelected)
        {
            if (DateTime.UtcNow - nativeInteractionAt < TimeSpan.FromSeconds(2)) return;
            if (GameHelpers.TryClickNativeButton("ChocoboBreedNaming", null, 5))
            {
                nativeContextSelected = false;
                nativeNameSelectorOpened = true;
                nativeInteractionAt = DateTime.UtcNow;
                return;
            }
            InspectNativeState?.Invoke();
            Block("Selected registration form; the resulting registration window is captured for verification.");
            return;
        }
        var inventory = Plugin.GameGui.GetAddonByName("InventoryExpansion");
        if (inventory.IsNull || !inventory.IsVisible) inventory = Plugin.GameGui.GetAddonByName("InventoryLarge");
        if (current.Phase == BreedingPhase.RegistrationPendingCapture && current.PrimarySelection is { } form &&
            Plugin.TargetManager.Target?.BaseId == 1010465 && !inventory.IsNull && inventory.IsVisible && inventory.IsReady)
        {
            if (!nativeContextOpened)
            {
                var present = ChocoboInventoryModel.Enumerate(inventoryService).Any(item => item.ItemId == form.ItemId &&
                    (int)item.Container == form.Container && item.Slot == form.Slot);
                if (!present) { Block("The selected registration form changed before opening its context menu."); return; }
                FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance()
                    ->OpenForItemSlot(form.InventoryType, form.Slot, 0, ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)inventory.Address)->Id);
                nativeContextOpened = true;
                nativeInteractionAt = DateTime.UtcNow;
                return;
            }
            if (GameHelpers.IsAddonVisible("ContextMenu") && DateTime.UtcNow - nativeInteractionAt >= TimeSpan.FromSeconds(1))
            {
                var label = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(9072).Text.ToString();
                if (GameHelpers.TrySelectNativeListEntry("ContextMenu", label, () => characterStateService.SaveCurrent(state =>
                    {
                        state.ActionBaseline = form;
                        state.ActionRequestedAtUtc = DateTime.UtcNow;
                        state.ActionEvidenceAtUtc = DateTime.MinValue;
                    })))
                {
                    nativeContextSelected = true;
                    nativeInteractionAt = DateTime.UtcNow;
                }
                else { InspectNativeState?.Invoke(); Block("The native Select Chocobo list entry is unavailable or disabled."); }
            }
            return;
        }
        if (nativeInteractionAt != DateTime.MinValue)
        {
            if (DateTime.UtcNow - nativeInteractionAt < TimeSpan.FromSeconds(2)) return;
            if (!GameHelpers.IsAddonVisible("SelectString") && !GameHelpers.IsAddonVisible("SelectIconString") && !GameHelpers.IsAddonVisible("Talk") &&
                !GameHelpers.IsAddonVisible("ChocoboBreedRegistration"))
            {
                if (DateTime.UtcNow - nativeInteractionAt >= TimeSpan.FromSeconds(8))
                    nativeInteractionAt = DateTime.MinValue;
                return;
            }
            nativeUiStepActive = false;
            InspectNativeState?.Invoke();
            Block("Breeding service interaction captured; its native selection must be verified before dispatch.");
            return;
        }
        if (DateTime.UtcNow - nativeUiStepStarted < TimeSpan.FromSeconds(3)) return;
        if (chocoboStatsService.IsRefreshRunning) return;
        if (chocoboStatsService.IsRefreshComplete)
        {
            var info = Plugin.GameGui.GetAddonByName("GoldSaucerInfo");
            if (!info.IsNull && info.IsVisible) ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)info.Address)->Close(true);
        }
        if (!GameHelpers.IsPlayerAvailable() || GameHelpers.IsLifestreamBusy() ||
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInEvent]) return;
        if (buyingG1 || current.Phase is BreedingPhase.CoveringPendingCapture or BreedingPhase.AdoptionPendingCapture)
        {
            if (Plugin.ClientState.TerritoryType != 148)
            {
                if (!nativeBreederTravelStarted)
                    nativeBreederTravelStarted = GameHelpers.SendGameCommand("/li Bentbranch Meadows");
                return;
            }
            var breeder = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcResident>().GetRow(buyingG1 ? 1011585u : 1010472u).Singular.ToString();
            if (!GameHelpers.IsNearObject(breeder))
            {
                // Quest576's observed stable approach. Stay on foot to avoid the roof.
                if (!GameHelpers.IsMovementRunning()) GameHelpers.TryMoveCloseTo(buyingG1
                    ? new System.Numerics.Vector3(-53.23877f, -0.0029053604f, 66.056274f)
                    : new System.Numerics.Vector3(-53.26935f, 0.3093315f, 69.41321f), 3);
                return;
            }
            GameHelpers.StopMovement();
            if (GameHelpers.TargetAndInteract(breeder)) nativeInteractionAt = DateTime.UtcNow;
            return;
        }
        if (!GameHelpers.IsInChocoboSquare())
        {
            if (!nativeBreederTravelStarted)
                nativeBreederTravelStarted = GameHelpers.TryTravelToChocoboSquare();
            return;
        }
        var npcName = current.Phase == BreedingPhase.PurchasingSupplies ? "Tack & Feed Trader"
            : Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcResident>().GetRow(nativeTutorial ? 1010464u : 1010465u).Singular.ToString();
        if (!GameHelpers.IsNearObject(npcName))
        {
            if (!GameHelpers.IsMovementRunning()) GameHelpers.TryMoveCloseTo(npcName, 3);
            return;
        }
        GameHelpers.StopMovement();
        if (GameHelpers.TargetAndInteract(npcName)) nativeInteractionAt = DateTime.UtcNow;
    }

    private bool UpdateAdoption(BreedingCharacterState state)
    {
        if (state.CollectionBaseline is { } baseline)
        {
            if (baseline.Pedigree is < 2 or > 9 || baseline.Proof.ItemId != ChocoboInventoryModel.ProofOfCoveringItemId ||
                baseline.Proof.Quantity != 1 || baseline.Proof.Slot < 0)
            { Block("Saved collection evidence is invalid; no collection will be replayed."); return true; }
            var maleId = ChocoboInventoryModel.FirstFledglingItemId + (uint)baseline.Pedigree - 1;
            var femaleId = ChocoboInventoryModel.FirstFemaleFledglingItemId + (uint)baseline.Pedigree - 1;
            var maleCount = inventoryService.GetItemCount(maleId);
            var femaleCount = inventoryService.GetItemCount(femaleId);
            var resultId = maleCount == baseline.MaleQuantity + 1 && femaleCount == baseline.FemaleQuantity ? maleId
                : femaleCount == baseline.FemaleQuantity + 1 && maleCount == baseline.MaleQuantity ? femaleId : 0;
            if (inventoryService.GetItemCount(ChocoboInventoryModel.ProofOfCoveringItemId) == 0 && resultId != 0)
            {
                bool? productionMatch = null;
                if (state.OffspringGoal != OffspringGoal.ReachPedigree)
                {
                    var additions = ChocoboInventoryModel.Enumerate(inventoryService).Where(form =>
                        form.Kind == ChocoboFormKind.Fledgling && form.ItemId == resultId && form.Quantity == 1 &&
                        !baseline.ExistingOffspring.Any(old => old.ItemId == form.ItemId && old.Container == (int)form.Container &&
                            old.Slot == form.Slot && old.Quantity == form.Quantity && old.Condition == form.Capacity)).ToArray();
                    if (additions.Length != 1 || (productionMatch = MatchesProductionGoal(state, additions[0])) == null)
                    {
                        Block("Collection occurred, but the exact new offspring and its trait must be reconciled before counting or another covering.");
                        return true;
                    }
                }
                log.Information($"[ChokeAbo][Native] Collection verified: proof consumed; G{baseline.Pedigree} form {resultId} increased by one.");
                InspectNativeState?.Invoke();
                StopOwnedFeedServices();
                characterStateService.TransitionCurrent(BreedingPhase.Planning, current =>
                {
                    // Save the count and consume the evidence together; a reload cannot count this collection twice.
                    if (productionMatch.HasValue)
                    {
                        current.OffspringCollected++;
                        if (productionMatch.Value && current.OffspringGoal == OffspringGoal.AbilityOffspring) current.AbilityOffspringProduced++;
                        if (productionMatch.Value && current.OffspringGoal == OffspringGoal.ColourOffspring) current.ColourOffspringProduced++;
                    }
                    current.CollectionBaseline = null;
                    current.ActionBaseline = null;
                    current.ActionEvidenceAtUtc = DateTime.UtcNow;
                    current.CoveringEligibleAtUtc = DateTime.MinValue;
                    current.BlockReason = string.Empty;
                });
                if (productionMatch.HasValue)
                    log.Information($"[ChokeAbo][Production] Collected G9 form {resultId}; match={productionMatch.Value}; produced={ProducedOffspring(state)}/{RequestedOffspring(state)}; retained unregistered.");
                return true;
            }
            if (DateTime.UtcNow - state.ActionRequestedAtUtc < TimeSpan.FromSeconds(15)) return true;
            InspectNativeState?.Invoke();
            Block("Collection was requested; proof consumption and one matching fledgling must be reconciled before another request.");
            return true;
        }
        var proofs = ChocoboInventoryModel.Enumerate(inventoryService).Where(form => form.Kind == ChocoboFormKind.Proof).ToArray();
        if (state.CoveringEligibleAtUtc == DateTime.MinValue || DateTime.UtcNow < state.CoveringEligibleAtUtc ||
            proofs.Length != 1 || proofs[0].Quantity != 1 || state.PrimarySelection is not { } parent ||
            !ChocoboInventoryForm.TryDecode(new InventoryItemSnapshot(parent.ItemId, string.Empty, parent.Condition,
                parent.Quantity, parent.InventoryType, parent.Slot), out var parentForm) || parentForm.Kind != ChocoboFormKind.Retired ||
            parentForm.Pedigree is < 1 or > 9 || parentForm.Pedigree == 9 && state.OffspringGoal == OffspringGoal.ReachPedigree)
        { Block("Collection requires the existing eligible covering, its retained-parent pedigree and exactly one Proof of Covering."); return true; }
        var pedigree = Math.Min(9, parentForm.Pedigree + 1);
        if (Plugin.TargetManager.Target?.BaseId == 1010472 &&
            GameHelpers.TrySelectNativeListEntry("SelectIconString", "Fledgling Adoption", () =>
                characterStateService.SaveCurrent(current =>
                {
                    current.CollectionBaseline = new CollectionEvidence
                    {
                        Proof = SelectedItemEvidence.From(proofs[0]), Pedigree = pedigree,
                        MaleQuantity = inventoryService.GetItemCount(ChocoboInventoryModel.FirstFledglingItemId + (uint)pedigree - 1),
                        FemaleQuantity = inventoryService.GetItemCount(ChocoboInventoryModel.FirstFemaleFledglingItemId + (uint)pedigree - 1),
                        ExistingOffspring = state.OffspringGoal == OffspringGoal.ReachPedigree ? new()
                            : ChocoboInventoryModel.Enumerate(inventoryService).Where(form => form.Kind == ChocoboFormKind.Fledgling && form.Pedigree == pedigree)
                                .Select(SelectedItemEvidence.From).ToList(),
                    };
                    current.ActionRequestedAtUtc = DateTime.UtcNow;
                    current.ActionEvidenceAtUtc = DateTime.MinValue;
                }), observedNodeId: 3))
        {
            nativeInteractionAt = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    private unsafe void ReconcileCancelledCollection(BreedingCharacterState state)
    {
        if (state.CollectionBaseline is not { } baseline || !GameHelpers.IsPlayerAvailable() ||
            GameHelpers.IsAddonVisible("Talk") || GameHelpers.IsAddonVisible("SelectIconString") || GameHelpers.IsAddonVisible("SelectYesno")) return;
        var proof = inventoryService.FindItem(baseline.Proof.InventoryType, baseline.Proof.Slot);
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventory.Instance();
        if (agent == null || agent->CurrentInventoryContextEvent != null || proof is not { } live ||
            live.ItemId != baseline.Proof.ItemId || live.Quantity != baseline.Proof.Quantity || live.Condition != baseline.Proof.Condition ||
            inventoryService.GetItemCount(ChocoboInventoryModel.FirstFledglingItemId + (uint)baseline.Pedigree - 1) != baseline.MaleQuantity ||
            inventoryService.GetItemCount(ChocoboInventoryModel.FirstFemaleFledglingItemId + (uint)baseline.Pedigree - 1) != baseline.FemaleQuantity) return;
        characterStateService.TransitionCurrent(BreedingPhase.AdoptionPendingCapture, current =>
        {
            current.CollectionBaseline = null;
            current.ActionEvidenceAtUtc = DateTime.UtcNow;
        });
        log.Information("[ChokeAbo][Native] Interrupted collection reconciled: original proof and fledgling counts unchanged, native interaction released.");
    }

    private unsafe bool IsOwnedCoveringConfirmation(BreedingCharacterState state, out uint fee)
    {
        fee = 0;
        if (state.ActionBaseline?.ItemId != ChocoboInventoryModel.ProofOfCoveringItemId ||
            !TryReadSelectedCovering(state, out fee)) return false;
        var handle = Plugin.GameGui.GetAddonByName("SelectYesno");
        if (handle.IsNull || !handle.IsVisible || !handle.IsReady) return false;
        var confirm = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)handle.Address;
        var coupling = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)Plugin.GameGui.GetAddonByName("ChocoboBreedCoupling").Address;
        var node = confirm->GetTextNodeById(2);
        if (confirm->BlockedParentId != coupling->Id || node == null) return false;
        var text = Dalamud.Game.Text.SeStringHandling.SeString.Parse(node->NodeText.AsSpan()).TextValue;
        return text == $"Proceed with the covering for {fee:N0} gil?" || text == $"Proceed with the covering for {fee} gil?";
    }

    private unsafe bool TryReadSelectedCovering(BreedingCharacterState state, out uint fee)
    {
        fee = 0;
        if (Plugin.TargetManager.Target?.BaseId != 1010472 || state.PrimarySelection is not { } primary ||
            state.PartnerSelection is not { } partner || inventoryService.GetItemCount(ChocoboInventoryModel.ProofOfCoveringItemId) != 0)
            return false;
        var handle = Plugin.GameGui.GetAddonByName("ChocoboBreedCoupling");
        if (handle.IsNull || !handle.IsVisible || !handle.IsReady || handle.AtkValuesCount != 26) return false;
        var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)handle.Address;
        var selected = new[] { inventoryService.FindItem(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.BlockedItems, 0),
            inventoryService.FindItem(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.BlockedItems, 1) };
        bool Matches(SelectedItemEvidence item) => selected.Any(slot => slot is { } value && value.ItemId == item.ItemId &&
            value.Condition == item.Condition && value.Quantity == item.Quantity) &&
            inventoryService.FindItem(item.InventoryType, item.Slot) is { } original && original.ItemId == item.ItemId &&
            original.Condition == item.Condition && original.Quantity == item.Quantity;
        if (!Matches(primary) || !Matches(partner)) return false;
        var feeNode = addon->GetTextNodeById(21);
        var label = addon->GetTextNodeById(20);
        if (feeNode == null || label == null || !feeNode->IsVisible() || label->NodeText.ToString() != "Covering Fee") return false;
        var feeText = Dalamud.Game.Text.SeStringHandling.SeString.Parse(feeNode->NodeText.AsSpan()).TextValue;
        return feeText.EndsWith('\uE049') && uint.TryParse(feeText[..^1].Replace(",", ""), out fee) && fee != 0;
    }

    private bool TryCommenceCovering(BreedingCharacterState state)
    {
        if (!TryReadSelectedCovering(state, out var fee)) return false;
        if (!CanSpend(inventoryService.GetItemCount(1), fee, state.GilReserve))
        {
            StopOwnedFeedServices();
            characterStateService.TransitionCurrent(BreedingPhase.Paused, current =>
            {
                current.PhaseBeforePause = BreedingPhase.Planning;
                current.BlockReason = "The required covering fee would cross the gil reserve. Explicit Resume is required.";
            });
            return true;
        }
        characterStateService.SaveCurrent(current =>
        {
            current.ActionBaseline = new SelectedItemEvidence { ItemId = ChocoboInventoryModel.ProofOfCoveringItemId, Quantity = 0 };
            current.ActionRequestedAtUtc = DateTime.UtcNow;
            current.ActionEvidenceAtUtc = DateTime.MinValue;
        });
        log.Information($"[ChokeAbo][Native] Covering selected stock verified: items={state.PrimarySelection!.ItemId}/{state.PartnerSelection!.ItemId}; fee={fee}; reserve={state.GilReserve}.");
        if (GameHelpers.TryClickNativeButton("ChocoboBreedCoupling", "Commence", 28)) return true;
        characterStateService.SaveCurrent(current => { current.ActionBaseline = null; current.ActionEvidenceAtUtc = DateTime.UtcNow; });
        return false;
    }

    private unsafe bool TryConfirmRegistration(BreedingCharacterState state)
    {
        var naming = Plugin.GameGui.GetAddonByName("ChocoboBreedNaming");
        var confirm = Plugin.GameGui.GetAddonByName("SelectYesno");
        if (naming.IsNull || !naming.IsVisible || confirm.IsNull || !confirm.IsVisible || !confirm.IsReady ||
            state.ActionBaseline is not { } form || Plugin.TargetManager.Target?.BaseId != 1010465) return false;
        var dialog = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)confirm.Address;
        var namingAddon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)naming.Address;
        // Both word controls are observed in fselector.md. Compare the owned
        // confirmation to the words currently displayed, including random names.
        var firstWord = PopupCaptureRecorder.GetNodeText(namingAddon->GetNodeById(5)).Trim();
        var secondWord = PopupCaptureRecorder.GetNodeText(namingAddon->GetNodeById(8)).Trim();
        if (string.IsNullOrWhiteSpace(firstWord) || string.IsNullOrWhiteSpace(secondWord) ||
            dialog->BlockedParentId != namingAddon->Id ||
            dialog->GetTextNodeById(2) == null ||
            dialog->GetTextNodeById(2)->NodeText.ToString() != $"Register the race chocobo {firstWord} {secondWord}?") return false;
        if (!ChocoboInventoryModel.Enumerate(inventoryService).Any(item => item.ItemId == form.ItemId &&
            (int)item.Container == form.Container && item.Slot == form.Slot && item.Quantity == form.Quantity)) return false;
        characterStateService.SaveCurrent(current => current.ActionRequestedAtUtc = DateTime.UtcNow);
        return GameHelpers.TryClickNativeButton("SelectYesno", "Yes");
    }

    private static unsafe bool IsRetirementConfirmationVisible()
    {
        if (Plugin.TargetManager.Target?.BaseId != 1010465) return false;
        var confirm = Plugin.GameGui.GetAddonByName("SelectYesno");
        if (confirm.IsNull || !confirm.IsVisible || !confirm.IsReady) return false;
        var dialog = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)confirm.Address;
        var node = dialog->GetTextNodeById(2);
        if (node == null) return false;
        var text = System.Text.RegularExpressions.Regex.Replace(
            Dalamud.Game.Text.SeStringHandling.SeString.Parse(node->NodeText.AsSpan()).TextValue, @"\s+", " ").Trim();
        return text.StartsWith("Retire ", StringComparison.Ordinal) &&
            text.EndsWith("※Retired chocobos cannot be re-registered as race chocobos.", StringComparison.Ordinal);
    }

    private void StartRequiredSupplyPurchase(BreedingCharacterState state)
    {
        var buyingG1 = state.BreedingMode == BreedingMode.OwnedParents &&
            state.RequiredPurchaseItemId is ChocoboInventoryModel.FirstFledglingItemId or ChocoboInventoryModel.FirstFemaleFledglingItemId;
        var buyingPermit = state.BreedingMode == BreedingMode.NpcPermits &&
            state.RequiredPurchaseItemId is >= ChocoboInventoryModel.FirstCoveringPermissionItemId and <= ChocoboInventoryModel.LastCoveringPermissionItemId;
        if (!buyingG1 && !buyingPermit || Plugin.TargetManager.Target?.BaseId != (buyingG1 ? 1011585u : 1010488u))
        { Block("The required supply does not match the selected breeding mode and supplier."); return; }
        // Re-plan immediately before spending so stale parent or permit evidence cannot buy a duplicate.
        var plan = PlanCurrentWorkflow(state,
            chocoboStatsService.ReadActiveRacerSnapshot(), ChocoboInventoryModel.Enumerate(inventoryService));
        if (plan.Action != (buyingG1 ? TargetPlanAction.BuyRegistrationForm : TargetPlanAction.BuyCoveringPermit) || plan.RequiredItemId != state.RequiredPurchaseItemId)
        { StopOwnedFeedServices(); characterStateService.TransitionCurrent(BreedingPhase.Planning); return; }
        for (var row = 0; row < 122; ++row)
        {
            var mapped = buyingG1 ? GameHelpers.TryReadGilShopEntry(row, out var itemId, out var price)
                : GameHelpers.TryReadMgpShopEntry(row, out itemId, out price);
            if (!mapped || itemId != state.RequiredPurchaseItemId) continue;
            var currency = buyingG1 ? "gil" : "MGP";
            var reserve = buyingG1 ? state.GilReserve : state.MgpReserve;
            if (!CanSpend(inventoryService.GetItemCount(buyingG1 ? 1u : 29u), price, reserve))
            { PauseRequiredSupply($"The required breeding supply would cross the {currency} reserve."); return; }
            if (inventoryService.GetFreeMainInventorySlotCount() == 0)
            { PauseRequiredSupply("A free main-inventory slot is required for the breeding supply."); return; }
            var quantity = inventoryService.GetItemCount(itemId);
            var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemId).Name.ToString();
            var entry = new FeedPurchaseEntry(default, "Breeding supply", name, itemId, checked((int)quantity + 1), quantity, 1,
                0, buyingG1 ? FeedCurrencyKind.Gil : FeedCurrencyKind.Mgp, buyingG1 ? "Feathertrader" : "Race Items", 1,
                buyingG1 ? "Shop" : "ShopExchangeCurrency", 0, row, price);
            characterStateService.SaveCurrent(current =>
            {
                current.ActionBaseline = new SelectedItemEvidence { ItemId = itemId, Quantity = quantity };
                current.ActionRequestedAtUtc = DateTime.UtcNow;
                current.ActionEvidenceAtUtc = DateTime.MinValue;
                current.BlockReason = $"Buying {name} for {price} {currency} within reserves; awaiting its inventory result.";
            });
            ownsFeedServices = true;
            feedActionStartedThisLoad = true;
            vendorPurchaseService.Reset();
            vendorPurchaseService.Start(new FeedPurchasePlan(new[] { entry }, inventoryService.GetItemCount(1),
                inventoryService.GetItemCount(29), buyingG1 ? price : 0, buyingG1 ? 0 : price, 0, 0, true),
                state.GilReserve, state.MgpReserve, verifyShopEntry: true, useOpenShop: true);
            log.Information($"[ChokeAbo][Native] Required supply verified against live shop: item={itemId}; row={row}; price={price} {currency}; reserve={reserve}.");
            return;
        }
        InspectNativeState?.Invoke();
        Block("The required breeding supply was not found in the verified native shop.");
    }

    private void UpdateRequiredSupplyPurchase(BreedingCharacterState state)
    {
        var baseline = state.ActionBaseline!;
        if (inventoryService.GetItemCount(baseline.ItemId) > baseline.Quantity)
        {
            if (baseline.ItemId is ChocoboInventoryModel.FirstFledglingItemId or ChocoboInventoryModel.FirstFemaleFledglingItemId)
            {
                if (!nativeSupplyCloseRequested)
                {
                    nativeSupplyCloseRequested = GameHelpers.TryCloseFeathertraderShop();
                    if (!nativeSupplyCloseRequested) return;
                }
                if (!GameHelpers.IsPlayerAvailable() ||
                    Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInEvent]) return;
            }
            else if (baseline.ItemId is >= ChocoboInventoryModel.FirstCoveringPermissionItemId and <= ChocoboInventoryModel.LastCoveringPermissionItemId)
            {
                // A late purchase refresh can show the shop again after receipt.
                // Keep the receipt and use the existing native action cadence until
                // the verified shop stays closed and its event ownership releases.
                if (!nativeSupplyCloseRequested || GameHelpers.IsAddonVisible("ShopExchangeCurrency"))
                {
                    if (nativeSupplyCloseRequested && DateTime.UtcNow - nativeInteractionAt < TimeSpan.FromSeconds(2)) return;
                    nativeSupplyCloseRequested = GameHelpers.TryClosePermitShop(baseline.ItemId);
                    if (!nativeSupplyCloseRequested) return;
                    nativeInteractionAt = DateTime.UtcNow;
                    return;
                }
                if (!GameHelpers.IsPlayerAvailable() ||
                    Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInEvent]) return;
            }
            log.Information($"[ChokeAbo][Native] Required supply purchase verified: item {baseline.ItemId} increased from {baseline.Quantity}.");
            characterStateService.SaveCurrent(current => { current.ActionBaseline = null; current.ActionEvidenceAtUtc = DateTime.UtcNow; });
            StopOwnedFeedServices();
            characterStateService.TransitionCurrent(BreedingPhase.Planning, current =>
            {
                current.RequiredPurchaseItemId = 0;
                current.BlockReason = string.Empty;
            });
            return;
        }
        if (vendorPurchaseService.IsFailed)
        {
            var reason = vendorPurchaseService.StatusText;
            if (!vendorPurchaseService.PurchaseWasDispatched)
                characterStateService.SaveCurrent(current => { current.ActionBaseline = null; current.ActionEvidenceAtUtc = DateTime.UtcNow; });
            InspectNativeState?.Invoke();
            PauseRequiredSupply($"Required supply purchase stopped: {reason}");
        }
        else if (!feedActionStartedThisLoad)
        {
            var plan = PlanCurrentWorkflow(state,
                chocoboStatsService.ReadActiveRacerSnapshot(), ChocoboInventoryModel.Enumerate(inventoryService));
            if (state.BreedingMode == BreedingMode.NpcPermits && plan.Action == TargetPlanAction.BuyCoveringPermit &&
                plan.RequiredItemId == baseline.ItemId)
            {
                for (var row = 0; row < 122; ++row)
                {
                    if (!GameHelpers.TryReadMgpShopEntry(row, out var itemId, out var price) || itemId != baseline.ItemId) continue;
                    var name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemId).Name.ToString();
                    var entry = new FeedPurchaseEntry(default, "Breeding supply", name, itemId, checked((int)baseline.Quantity + 1),
                        baseline.Quantity, 1, 0, FeedCurrencyKind.Mgp, "Race Items", 1, "ShopExchangeCurrency", 0, row, price);
                    if (!vendorPurchaseService.AdoptPendingRequiredPurchase(new FeedPurchasePlan(new[] { entry },
                        inventoryService.GetItemCount(1), inventoryService.GetItemCount(29), 0, price, 0, 0, true), state.GilReserve, state.MgpReserve)) break;
                    ownsFeedServices = true;
                    feedActionStartedThisLoad = true;
                    return;
                }
            }
            Block("A required-supply purchase is unverified after reload; its matching pending confirmation or inventory result is required before another purchase.");
        }
    }

    private void PauseRequiredSupply(string reason)
    {
        StopOwnedFeedServices();
        characterStateService.TransitionCurrent(BreedingPhase.Paused, current =>
        {
            current.PhaseBeforePause = BreedingPhase.PurchasingSupplies;
            current.BlockReason = $"{reason} Explicit Resume is required.";
        });
    }

    private void TransitionStable(BreedingPhase phase, string reason)
    {
        var state = characterStateService.GetCurrent();
        if (state.Phase == phase && state.BlockReason == reason) return;
        characterStateService.TransitionCurrent(phase, current =>
        {
            current.BlockReason = reason;
            current.PrimarySelection = null;
            current.PartnerSelection = null;
            current.ActionBaseline = null;
        });
    }

    public static bool CanSpend(uint balance, uint cost, uint reserve)
        => cost == 0 || (balance >= reserve && cost <= balance - reserve);

    private void HandleUnavailableFeed(BreedingCharacterState state, string reason)
    {
        if (state.ProtocolVersion < 3) { Block(reason); return; }
        StopOwnedFeedServices();
        if (state.FeedPolicy == InsufficientFeedPolicy.Stop)
        {
            characterStateService.TransitionCurrent(BreedingPhase.Paused, current =>
            {
                current.PhaseBeforePause = BreedingPhase.Planning;
                current.BlockReason = $"{reason} Feeding policy is Stop; explicit Resume is required.";
            });
            return;
        }
        var racer = chocoboStatsService.ReadActiveRacerSnapshot();
        if (!racer.IsLoaded) { Block("Cannot skip feeding without a current racer snapshot."); return; }
        characterStateService.TransitionCurrent(BreedingPhase.Planning, current =>
        {
            current.SkippedFeedPedigree = racer.Pedigree;
            current.SkippedFeedRank = racer.Rank;
            current.BlockReason = $"Skipped this feeding round: {reason}";
        });
        log.Information($"[ChokeAbo][Target] Skipped feeding at G{racer.Pedigree} rank {racer.Rank}: {reason}");
        AdvanceTargetCycle();
    }

    private void Block(string reason)
    {
        nativeUiStepActive = false;
        StopOwnedFeedServices();
        characterStateService.TransitionCurrent(BreedingPhase.Blocked, current => current.BlockReason = reason);
        log.Warning($"[ChokeAbo][Target] {reason}");
    }

    private void StopOwnedFeedServices()
    {
        if (nativeUiStepActive || ownsNativeCapture) CloseOwnedNativeInventory();
        if (ownsNativeCapture)
        {
            ownsNativeCapture = false;
            EndNativeCapture?.Invoke();
        }
        if (nativeUiStepActive)
        {
            nativeUiStepActive = false;
            GameHelpers.StopMovement();
        }
        if (!ownsFeedServices)
            return;

        CloseOwnedNativeInventory();
        vendorPurchaseService.Reset();
        stableFeedingService.Reset();
        ownsFeedServices = false;
        feedActionStartedThisLoad = false;
    }

    private unsafe void ReconcileInterruptedFeedTravel()
    {
        var state = characterStateService.GetCurrent();
        if (state.ExecutionOwner != BreedingExecutionOwner.Target || state.Phase != BreedingPhase.Blocked ||
            state.ActionBaseline != null || !state.BlockReason.StartsWith("Target feed purchase blocked:", StringComparison.Ordinal) ||
            GameHelpers.IsLifestreamBusy() || !GameHelpers.IsInChocoboSquare()) return;
        var travel = Plugin.GameGui.GetAddonByName("TelepotTown");
        if (travel.IsNull || !travel.IsVisible || !travel.IsReady) return;
        ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)travel.Address)->Close(true);
        log.Information("[ChokeAbo][Native] Closed interrupted feed travel destination window; waiting for world readiness.");
    }

    private unsafe void CloseOwnedNativeInventory()
    {
        var state = characterStateService.GetCurrent();
        var permitItemId = state.RequiredPurchaseItemId != 0 ? state.RequiredPurchaseItemId : state.PartnerSelection?.ItemId ?? 0;
        var permitReceived = state.ActionBaseline == null ||
            state.ActionBaseline is { } purchase && purchase.ItemId == permitItemId &&
            inventoryService.GetItemCount(permitItemId) > purchase.Quantity;
        if (state.ExecutionOwner == BreedingExecutionOwner.Target && state.BreedingMode == BreedingMode.NpcPermits &&
            permitReceived && Plugin.TargetManager.Target?.BaseId == 1010488 &&
            permitItemId is >= ChocoboInventoryModel.FirstCoveringPermissionItemId and <= ChocoboInventoryModel.LastCoveringPermissionItemId &&
            inventoryService.GetItemCount(permitItemId) > 0)
        { GameHelpers.TryClosePermitShop(permitItemId); return; }
        if (state.ExecutionOwner == BreedingExecutionOwner.Target && state.ActionBaseline == null &&
            (state.RequiredPurchaseItemId is ChocoboInventoryModel.FirstFledglingItemId or ChocoboInventoryModel.FirstFemaleFledglingItemId ||
                state.PrimarySelection?.ItemId is ChocoboInventoryModel.FirstFledglingItemId or ChocoboInventoryModel.FirstFemaleFledglingItemId) &&
            Plugin.TargetManager.Target?.BaseId == 1011585)
        { GameHelpers.TryCloseFeathertraderShop(); return; }
        if (state.ExecutionOwner == BreedingExecutionOwner.Target && state.PrimarySelection != null &&
            state.ActionBaseline == null && Plugin.TargetManager.Target?.BaseId == 1010472 &&
            GameHelpers.IsAddonVisible("ChocoboBreedCoupling"))
        {
            InspectNativeState?.Invoke();
            GameHelpers.TryClickNativeButton("ChocoboBreedCoupling", "Cancel", 29);
            return;
        }
        if (state.ExecutionOwner == BreedingExecutionOwner.Target && state.RequiredPurchaseItemId != 0 &&
            state.ActionBaseline == null &&
            (Plugin.TargetManager.Target?.BaseId == 1010488 || Plugin.TargetManager.Target?.BaseId == 1011585 &&
                state.RequiredPurchaseItemId is ChocoboInventoryModel.FirstFledglingItemId or ChocoboInventoryModel.FirstFemaleFledglingItemId))
        {
            foreach (var name in new[] { "Shop", "ShopExchangeCurrency", "SelectIconString" })
            {
                var addon = Plugin.GameGui.GetAddonByName(name);
                if (!addon.IsNull && addon.IsVisible) ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address)->Close(true);
            }
            return;
        }
        if (state.ExecutionOwner == BreedingExecutionOwner.Target &&
            state.ActionBaseline?.ItemId is >= ChocoboInventoryModel.FirstRetiredItemId and <= ChocoboInventoryModel.LastRetiredItemId &&
            Plugin.TargetManager.Target?.BaseId == 1010465 &&
            GameHelpers.TrySelectNativeListEntry("SelectString", "No, not yet! I am not ready to say good-bye!", () => { }))
            return;
        if (state.ExecutionOwner == BreedingExecutionOwner.Target && state.ActionBaseline == null &&
            IsRetirementConfirmationVisible())
        {
            GameHelpers.TryClickNativeButton("SelectYesno", "No", 11);
            return;
        }
        if (nativeTutorial && Plugin.TargetManager.Target?.BaseId == 1010464)
        {
            var menu = Plugin.GameGui.GetAddonByName("SelectString");
            if (!menu.IsNull && menu.IsVisible)
                ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)menu.Address)->Close(true);
        }
        if (state.ExecutionOwner != BreedingExecutionOwner.Target || Plugin.TargetManager.Target?.BaseId != 1010465) return;
        var ownsTrainerFeed = state.ActionBaseline is { } feed && IsKnownFeedItem(feed.ItemId) &&
            state.Phase is BreedingPhase.Feeding or BreedingPhase.Blocked or BreedingPhase.Paused;
        if (state.PrimarySelection == null && !ownsTrainerFeed) return;
        if (ownsTrainerFeed && GameHelpers.IsAddonVisible("ChocoboBreedTraining"))
        {
            GameHelpers.TryClickNativeButton("ChocoboBreedTraining", "Cancel");
            return;
        }
        if (state.ActionBaseline == null)
        {
            var menu = Plugin.GameGui.GetAddonByName("SelectIconString");
            if (!menu.IsNull && menu.IsVisible)
                ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)menu.Address)->Close(true);
        }
        // Closing an Atk window alone leaves the trainer's server-side selection
        // active. Notify the actual inventory session owner before hiding it.
        var inventoryAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventory.Instance();
        if (inventoryAgent != null && inventoryAgent->CurrentInventoryContextEvent != null &&
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInQuestEvent])
        {
            var closed = inventoryAgent->CurrentInventoryContextEvent->HandleClose();
            log.Information($"[ChokeAbo][Native] Owned inventory session HandleClose returned {closed}; awaiting world readiness.");
            inventoryAgent->Hide();
        }
        foreach (var name in new[] { "ContextMenu", "InventoryExpansion", "InventoryLarge" })
        {
            var addon = Plugin.GameGui.GetAddonByName(name);
            if (!addon.IsNull && addon.IsVisible) ((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addon.Address)->Close(true);
        }
    }

    private static bool CanReachFeedServices(bool needsVendor, out string reason)
    {
        try
        {
            var navReady = Plugin.PluginInterface
                .GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady")
                .InvokeFunc();
            if (!navReady)
            {
                reason = "vnavmesh is unavailable or not ready for target feeding.";
                return false;
            }

            if (!GameHelpers.IsInChocoboSquare())
            {
                Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
            }

            if (needsVendor && GameHelpers.IsInChocoboSquare() && GameHelpers.FindObjectByName("Tack & Feed Trader") == null)
            {
                reason = "The Tack & Feed Trader is unavailable for interaction.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch
        {
            reason = "Required Lifestream/vnavmesh vendor interaction is unavailable.";
            return false;
        }
    }

    private static bool TryGetCurrentIdentity(out ulong contentId)
    {
        contentId = Plugin.PlayerState.ContentId;
        return Plugin.ClientState.IsLoggedIn && contentId != 0;
    }

    private static string BuildCoveringWaitStatus(BreedingCharacterState state)
    {
        if (state.CoveringEligibleAtUtc == DateTime.MinValue)
            return "Covering wait has no valid eligibility evidence.";
        if (DateTime.UtcNow >= state.CoveringEligibleAtUtc)
            return "Covering is eligible; adoption is awaiting exact capture evidence.";

        return $"Covering wait until {state.CoveringEligibleAtUtc:u}.";
    }

    private TargetCycleStatus BuildProtocolBlock(ulong contentId, string reason)
        => new(
            TargetCycleProtocol.Version,
            contentId,
            TargetCycleProtocol.ToPhaseName(BreedingPhase.Blocked),
            true,
            false,
            false,
            reason,
            null);
}
