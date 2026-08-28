using Dalamud.Plugin.Services;

namespace ChokeAbo.Services;

public sealed class BreedingService
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

    public bool IsRunning
    {
        get
        {
            var state = characterStateService.GetCurrent();
            return state.ExecutionOwner == BreedingExecutionOwner.Target &&
                   state.Phase is BreedingPhase.Planning
                       or BreedingPhase.PurchasingFeed
                       or BreedingPhase.Feeding;
        }
    }

    public bool OwnsFeedServices => ownsFeedServices;

    public bool GameActionInProgress
    {
        get
        {
            var state = characterStateService.GetCurrent();
            return state.ExecutionOwner == BreedingExecutionOwner.Target &&
                   state.Phase is BreedingPhase.Planning
                       or BreedingPhase.PurchasingFeed
                       or BreedingPhase.Feeding;
        }
    }

    public string StatusText
    {
        get
        {
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
            current.BlockReason = "Manual breeding selection is waiting for David's retirement, covering, fledgling-selector, and adoption captures.";
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

        activeContentId = contentId;
        characterStateService.MigrateLegacyStateIfNeeded(DateTime.UtcNow);
        var state = characterStateService.GetOrCreateCurrent();
        var inputsChanged = state.ExecutionOwner == BreedingExecutionOwner.Target &&
                            (state.TargetPedigree != request.TargetPedigree ||
                             state.RetirementRank != request.RetirementRank ||
                             state.PreferredFeedGrade != request.PreferredFeedGrade);
        if (inputsChanged && state.Phase is BreedingPhase.PurchasingFeed or BreedingPhase.Feeding)
        {
            Block("Target inputs changed while an immediate feed action was awaiting evidence.");
            return GetTargetCycleStatus(contentId);
        }

        if (state.ExecutionOwner != BreedingExecutionOwner.Target || inputsChanged)
        {
            StopOwnedFeedServices();
            characterStateService.TransitionCurrent(BreedingPhase.Planning, current =>
            {
                current.ExecutionOwner = BreedingExecutionOwner.Target;
                current.TargetPedigree = request.TargetPedigree;
                current.RetirementRank = request.RetirementRank;
                current.PreferredFeedGrade = request.PreferredFeedGrade;
                current.PrimarySelection = null;
                current.PartnerSelection = null;
                current.ActionBaseline = null;
                current.CoveringPurpose = CoveringPurpose.None;
                current.PauseRequested = false;
                current.BlockReason = string.Empty;
            });
            feedActionStartedThisLoad = false;
        }
        else if (state.Phase == BreedingPhase.Paused)
        {
            characterStateService.TransitionCurrent(
                state.PhaseBeforePause is BreedingPhase.Idle or BreedingPhase.Paused
                    ? BreedingPhase.Planning
                    : state.PhaseBeforePause,
                current =>
                {
                    current.PauseRequested = false;
                    current.BlockReason = string.Empty;
                });
        }

        state = characterStateService.GetCurrent();
        if (state.Phase is BreedingPhase.PurchasingFeed or BreedingPhase.Feeding)
            return GetTargetCycleStatus(contentId);
        if (state.Phase == BreedingPhase.CoveringWait)
        {
            UpdateCoveringWait(state);
            return GetTargetCycleStatus(contentId);
        }
        if (state.Phase is BreedingPhase.RetirementPendingCapture
            or BreedingPhase.CoveringPendingCapture
            or BreedingPhase.AdoptionPendingCapture
            or BreedingPhase.RegistrationPendingCapture
            or BreedingPhase.Blocked)
        {
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

        characterStateService.SaveCurrent(current => current.PauseRequested = true);
        PauseAtSafeBoundaryIfRequested();
        return GetTargetCycleStatus(contentId);
    }

    public TargetCycleStatus GetTargetCycleStatus(ulong contentId)
    {
        var currentContentId = Plugin.PlayerState.ContentId;
        if (contentId == 0 || contentId != currentContentId)
            return BuildProtocolBlock(currentContentId, "Status Content ID does not match the current character.");

        var state = characterStateService.GetCurrent();
        var targetReady = state.ExecutionOwner == BreedingExecutionOwner.Target && state.Phase == BreedingPhase.TargetReady;
        var gameAction = GameActionInProgress;
        var shouldBlock = state.ExecutionOwner == BreedingExecutionOwner.Target && state.Phase switch
        {
            BreedingPhase.Racing or BreedingPhase.TargetReady => false,
            BreedingPhase.CoveringWait when state.CoveringPurpose == CoveringPurpose.ProduceMissingSex => false,
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
                : null);
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
        if (!TryGetCurrentIdentity(out var contentId))
            return;

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

        PauseAtSafeBoundaryIfRequested();
        state = characterStateService.GetCurrent();
        if (state.Phase == BreedingPhase.Paused)
            return;

        switch (state.Phase)
        {
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
        if (characterStateService.GetCurrent().ExecutionOwner == BreedingExecutionOwner.Target)
            PauseTargetCycle(Plugin.PlayerState.ContentId);
        else
            StopOwnedFeedServices();
    }

    private void AdvanceTargetCycle()
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
        var forms = ChocoboInventoryModel.Enumerate(inventoryService);
        var plan = TargetPedigreePlanner.Plan(state.TargetPedigree, state.RetirementRank, racer, forms);
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

    private void StartTargetFeedRound(BreedingCharacterState state)
    {
        var stat = chocoboStatsService.SelectLowestEligibleStat();
        if (stat == null)
        {
            characterStateService.TransitionCurrent(BreedingPhase.Planning, current => current.BlockReason = string.Empty);
            AdvanceTargetCycle();
            return;
        }

        var plan = chocoboStatsService.BuildSingleTargetFeedPlan(stat.Value, state.PreferredFeedGrade);
        var entry = plan.Entries.SingleOrDefault();
        if (entry == null || entry.ItemId == 0)
        {
            Block("The selected target feed item could not be resolved.");
            return;
        }
        if (!plan.CanAffordGil || !plan.CanAffordMgp)
        {
            Block($"Insufficient {(entry.CurrencyKind == FeedCurrencyKind.Gil ? "gil" : "MGP")} for one confirmed {entry.FeedName} round.");
            return;
        }
        if (entry.QuantityToBuy > 0 && inventoryService.GetFreeMainInventorySlotCount() == 0)
        {
            Block("No free main-inventory slot is available for target feed.");
            return;
        }
        if (!CanReachFeedServices(entry.QuantityToBuy > 0, out var availabilityReason))
        {
            Block(availabilityReason);
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
                current.BaselineSessionsAvailable = chocoboStatsService.Snapshot.SessionsAvailable;
                current.ActionRequestedAtUtc = DateTime.UtcNow;
                current.ActionEvidenceAtUtc = DateTime.MinValue;
                current.BlockReason = $"Buying one {entry.FeedName} for the lowest-ratio {entry.StatLabel} round.";
            });
            ownsFeedServices = true;
            feedActionStartedThisLoad = true;
            vendorPurchaseService.Reset();
            vendorPurchaseService.Start(plan);
            return;
        }

        StartPersistedFeedAction(plan, baseline);
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

            var plan = chocoboStatsService.BuildSingleTargetFeedPlan(stat.Value, state.PreferredFeedGrade);
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
            current.BlockReason = "Covering is eligible; exact adoption remains blocked until adoption.md is supplied.";
        });
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
            current.BlockReason = $"{plan.Reason} Exact UI selection is blocked until {captureFile} is supplied.";
        });
    }

    private void TransitionStable(BreedingPhase phase, string reason)
        => characterStateService.TransitionCurrent(phase, current =>
        {
            current.BlockReason = reason;
            current.PrimarySelection = null;
            current.PartnerSelection = null;
            current.ActionBaseline = null;
        });

    private void Block(string reason)
    {
        StopOwnedFeedServices();
        characterStateService.TransitionCurrent(BreedingPhase.Blocked, current => current.BlockReason = reason);
        log.Warning($"[ChokeAbo][Target] {reason}");
    }

    private void StopOwnedFeedServices()
    {
        if (!ownsFeedServices)
            return;

        vendorPurchaseService.Reset();
        stableFeedingService.Reset();
        ownsFeedServices = false;
        feedActionStartedThisLoad = false;
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
