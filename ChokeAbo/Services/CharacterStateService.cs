using FFXIVClientStructs.FFXIV.Client.Game;

namespace ChokeAbo.Services;

public enum BreedingPhase
{
    Idle = 0,
    LegacyPreparingCovering = 1,
    LegacyCovering = 2,
    LegacyFledglingReady = 3,
    LegacyRegistering = 4,
    LegacyRegistered = 5,
    Planning = 10,
    PurchasingFeed = 11,
    Feeding = 12,
    Racing = 13,
    RetirementPendingCapture = 14,
    CoveringPendingCapture = 15,
    CoveringWait = 16,
    AdoptionPendingCapture = 17,
    RegistrationPendingCapture = 18,
    Paused = 19,
    Blocked = 20,
    TargetReady = 21,
}

public enum BreedingExecutionOwner
{
    None,
    Manual,
    Target,
}

[Serializable]
public sealed class SelectedItemEvidence
{
    public uint ItemId { get; set; }
    public int Container { get; set; }
    public int Slot { get; set; } = -1;
    public uint Quantity { get; set; }
    public uint Condition { get; set; }

    public static SelectedItemEvidence From(ChocoboInventoryForm form)
        => new()
        {
            ItemId = form.ItemId,
            Container = (int)form.Container,
            Slot = form.Slot,
            Quantity = form.Quantity,
            Condition = form.Capacity,
        };

    public static SelectedItemEvidence From(InventoryItemSnapshot item)
        => new()
        {
            ItemId = item.ItemId,
            Container = (int)item.Container,
            Slot = item.Slot,
            Quantity = item.Quantity,
            Condition = item.Condition,
        };

    public InventoryType InventoryType => (InventoryType)Container;
}

[Serializable]
public sealed class BreedingCharacterState
{
    public BreedingExecutionOwner ExecutionOwner { get; set; }
    public BreedingPhase Phase { get; set; }
    public BreedingPhase PhaseBeforePause { get; set; } = BreedingPhase.Idle;
    public int TargetPedigree { get; set; }
    public int RetirementRank { get; set; }
    public int PreferredFeedGrade { get; set; }
    public SelectedItemEvidence? PrimarySelection { get; set; }
    public SelectedItemEvidence? PartnerSelection { get; set; }
    public SelectedItemEvidence? ActionBaseline { get; set; }
    public uint BaselineSessionsAvailable { get; set; }
    public CoveringPurpose CoveringPurpose { get; set; }
    public bool PauseRequested { get; set; }
    public string BlockReason { get; set; } = string.Empty;
    public DateTime LastTransitionUtc { get; set; } = DateTime.MinValue;
    public DateTime ActionRequestedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime ActionEvidenceAtUtc { get; set; } = DateTime.MinValue;
    public DateTime CoveringEligibleAtUtc { get; set; } = DateTime.MinValue;
    public bool CoveringWaitMigratedTo24Hours { get; set; }

    // I237 pre-target fields are retained only for one-way migration of dirty/live state.
    public DateTime ReadyAtUtc { get; set; } = DateTime.MinValue;
    public uint RetiredRegistrationItemId { get; set; }
    public uint PartnerItemId { get; set; }
    public uint ExpectedFledglingItemId { get; set; }
}

public sealed class CharacterStateService
{
    private static readonly DateTime EarliestValidTransitionUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Configuration configuration;

    public CharacterStateService(Configuration configuration)
    {
        this.configuration = configuration;
    }

    public string Summary => "Per-Content-ID target-cycle ownership, exact item evidence, and transition boundaries.";

    public BreedingCharacterState GetCurrent()
        => Get(Plugin.PlayerState.ContentId);

    public BreedingCharacterState Get(ulong contentId)
        => contentId != 0 && configuration.BreedingStates.TryGetValue(contentId, out var state)
            ? state
            : new BreedingCharacterState();

    public BreedingCharacterState GetOrCreateCurrent()
    {
        var contentId = Plugin.PlayerState.ContentId;
        if (contentId == 0)
            return new BreedingCharacterState();

        if (!configuration.BreedingStates.TryGetValue(contentId, out var state))
        {
            state = new BreedingCharacterState();
            configuration.BreedingStates[contentId] = state;
        }

        return state;
    }

    public bool TransitionCurrent(BreedingPhase phase, Action<BreedingCharacterState>? update = null)
    {
        var contentId = Plugin.PlayerState.ContentId;
        if (contentId == 0)
            return false;

        var state = GetOrCreateCurrent();
        update?.Invoke(state);
        state.Phase = phase;
        state.LastTransitionUtc = DateTime.UtcNow;
        configuration.Save();
        return true;
    }

    public bool SaveCurrent(Action<BreedingCharacterState> update)
    {
        if (Plugin.PlayerState.ContentId == 0)
            return false;

        update(GetOrCreateCurrent());
        configuration.Save();
        return true;
    }

    public void MigrateLegacyStateIfNeeded(DateTime nowUtc)
    {
        var contentId = Plugin.PlayerState.ContentId;
        if (contentId == 0 || !configuration.BreedingStates.TryGetValue(contentId, out var state))
            return;

        if (state.Phase == BreedingPhase.LegacyCovering && !state.CoveringWaitMigratedTo24Hours)
        {
            var originalTransition = state.LastTransitionUtc;
            var transitionIsValid = originalTransition.Kind != DateTimeKind.Unspecified &&
                                    originalTransition.ToUniversalTime() >= EarliestValidTransitionUtc &&
                                    originalTransition.ToUniversalTime() <= nowUtc.AddMinutes(5);
            state.CoveringEligibleAtUtc = transitionIsValid
                ? originalTransition.ToUniversalTime().AddHours(24)
                : nowUtc.AddHours(24);
            state.CoveringWaitMigratedTo24Hours = true;
            state.Phase = BreedingPhase.CoveringWait;
            state.ExecutionOwner = BreedingExecutionOwner.Target;
            state.BlockReason = string.Empty;
            configuration.Save();
            return;
        }

        if (state.Phase is BreedingPhase.LegacyPreparingCovering
            or BreedingPhase.LegacyFledglingReady
            or BreedingPhase.LegacyRegistering
            or BreedingPhase.LegacyRegistered)
        {
            state.ExecutionOwner = BreedingExecutionOwner.Target;
            state.Phase = BreedingPhase.Blocked;
            state.BlockReason = "Legacy breeding action cannot be replayed because its exact UI evidence is unavailable.";
            configuration.Save();
        }
    }

    public bool ResetCurrent()
        => TransitionCurrent(BreedingPhase.Idle, state =>
        {
            state.ExecutionOwner = BreedingExecutionOwner.None;
            state.PhaseBeforePause = BreedingPhase.Idle;
            state.TargetPedigree = 0;
            state.RetirementRank = 0;
            state.PreferredFeedGrade = 0;
            state.PrimarySelection = null;
            state.PartnerSelection = null;
            state.ActionBaseline = null;
            state.BaselineSessionsAvailable = 0;
            state.CoveringPurpose = CoveringPurpose.None;
            state.PauseRequested = false;
            state.BlockReason = string.Empty;
            state.ActionRequestedAtUtc = DateTime.MinValue;
            state.ActionEvidenceAtUtc = DateTime.MinValue;
            state.CoveringEligibleAtUtc = DateTime.MinValue;
        });
}
