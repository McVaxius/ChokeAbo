using Dalamud.Game.ClientState.Conditions;

namespace ChokeAbo.Services;

internal sealed record StockCleanupPreview(ulong ContentId, uint TerritoryId,
    StockCleanupProtection Protection, IReadOnlyList<InventoryItemSnapshot> Items);

internal sealed record StockCleanupProtection(BreedingPhase Phase, BreedingPhase PreviousPhase,
    BreedingExecutionOwner Owner, uint PurchaseItemId, DateTime ActionAt, DateTime CoveringAt,
    int CollectionPedigree, uint CollectionMale, uint CollectionFemale,
    IReadOnlyList<(uint Item, int Container, int Slot, uint Quantity, uint Condition)> Inputs)
{
    internal static StockCleanupProtection Capture(BreedingCharacterState state)
    {
        static (uint, int, int, uint, uint) Read(SelectedItemEvidence? item)
            => item == null ? (0, 0, -1, 0, 0) : (item.ItemId, item.Container, item.Slot, item.Quantity, item.Condition);
        var inputs = new[] { state.PrimarySelection, state.PartnerSelection, state.ActionBaseline, state.CollectionBaseline?.Proof }
            .Concat(state.CollectionBaseline?.ExistingOffspring ?? []).Select(Read).ToArray();
        return new(state.Phase, state.PhaseBeforePause, state.ExecutionOwner, state.RequiredPurchaseItemId,
            state.ActionRequestedAtUtc, state.CoveringEligibleAtUtc, state.CollectionBaseline?.Pedigree ?? 0,
            state.CollectionBaseline?.MaleQuantity ?? 0, state.CollectionBaseline?.FemaleQuantity ?? 0, inputs);
    }

    internal bool Matches(StockCleanupProtection current)
        => Phase == current.Phase && PreviousPhase == current.PreviousPhase && Owner == current.Owner
           && PurchaseItemId == current.PurchaseItemId && ActionAt == current.ActionAt && CoveringAt == current.CoveringAt
           && CollectionPedigree == current.CollectionPedigree && CollectionMale == current.CollectionMale
           && CollectionFemale == current.CollectionFemale && Inputs.SequenceEqual(current.Inputs);

    internal bool Protects(uint itemId) => itemId == PurchaseItemId || Inputs.Any(input => input.Item == itemId);
}

public sealed partial class BreedingService
{
    private StockCleanupPreview? stockCleanup;
    private int stockCleanupIndex;
    private long stockCleanupRemoved;
    internal long StockCleanupRemoved => stockCleanupRemoved;
    internal long StockCleanupTotal { get; private set; }
    private DateTime stockCleanupSentAt;
    internal bool StockCleanupRunning => stockCleanup != null;
    internal string StockCleanupStatus { get; private set; } = "Idle";

    internal static bool IsStockCleanupCandidate(ChocoboInventoryForm form, StockCleanupProtection protection,
        bool fledglings, bool retired, bool permissions)
        => form.Container is FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1
                          or FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2
                          or FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3
                          or FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4
           && form.Slot >= 0 && form.Quantity > 0 && form.Pedigree is >= 1 and <= 8
           && !protection.Protects(form.ItemId)
           && (form.Kind == ChocoboFormKind.Fledgling && fledglings
               || form.Kind == ChocoboFormKind.Retired && retired
               || form.Kind == ChocoboFormKind.CoveringPermission && permissions);

    internal StockCleanupPreview? PreviewStockCleanup(bool fledglings, bool retired, bool permissions)
    {
        if (StockCleanupRunning || !CanCleanStock())
        {
            StockCleanupStatus = "Breeding must be settled and G9 reached.";
            return null;
        }
        var protection = StockCleanupProtection.Capture(characterStateService.GetCurrent());
        var items = new List<InventoryItemSnapshot>();
        foreach (var form in ChocoboInventoryModel.Enumerate(inventoryService))
        {
            if (!IsStockCleanupCandidate(form, protection, fledglings, retired, permissions)) continue;
            if (!inventoryService.TryReadSlot(form.Container, form.Slot, out var item)
                || item is not { } snapshot || snapshot.Metadata.Length == 0
                || !ChocoboInventoryForm.TryDecode(snapshot, out var live) || live != form)
            {
                StockCleanupStatus = "Breeding, character or stock changed; cleanup stopped.";
                return null;
            }
            items.Add(snapshot);
        }
        StockCleanupStatus = items.Count == 0 ? "No eligible stock." : "Review the selected stock before discarding.";
        return new(Plugin.PlayerState.ContentId, Plugin.ClientState.TerritoryType, protection, items.ToArray());
    }

    internal bool StartStockCleanup(StockCleanupPreview preview, IReadOnlyList<InventoryItemSnapshot> selected)
    {
        if (StockCleanupRunning || selected.Count == 0 || selected.Distinct().Count() != selected.Count
            || selected.Any(item => !preview.Items.Contains(item)) || !StockCleanupStillValid(preview))
        {
            StockCleanupStatus = "Breeding, character or stock changed; cleanup stopped.";
            return false;
        }
        foreach (var item in selected)
            if (!inventoryService.TryReadSlot(item.Container, item.Slot, out var current) || current != item)
            {
                StockCleanupStatus = "Breeding, character or stock changed; cleanup stopped.";
                return false;
            }
        stockCleanup = preview with { Items = selected.ToArray() };
        stockCleanupIndex = 0;
        stockCleanupRemoved = 0;
        StockCleanupTotal = selected.Sum(item => (long)item.Quantity);
        stockCleanupSentAt = DateTime.MinValue;
        StockCleanupStatus = "Waiting for discard result...";
        return true;
    }

    internal void CancelStockCleanup()
    {
        if (stockCleanup == null) return;
        stockCleanup = null;
        StockCleanupStatus = "Cleanup cancelled.";
    }

    private bool CanCleanStock()
    {
        if (!configuration.PluginEnabled || !TryGetCurrentIdentity(out _) || !GameHelpers.IsPlayerAvailable()
            || Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.BoundByDuty]
            || Plugin.Condition[ConditionFlag.BoundByDuty56] || Plugin.Condition[ConditionFlag.BoundByDuty95]
            || nativeUiStepActive || ownsFeedServices || isOtherAutomationRunning()) return false;
        var state = characterStateService.GetCurrent();
        var phase = state.Phase == BreedingPhase.Paused ? state.PhaseBeforePause : state.Phase;
        if (phase is not (BreedingPhase.Idle or BreedingPhase.TargetReady)
            || state.ActionBaseline != null || state.CollectionBaseline != null) return false;
        var racer = chocoboStatsService.ReadActiveRacerSnapshot();
        return racer.IsLoaded && racer.Pedigree == 9 || ChocoboInventoryModel.Enumerate(inventoryService)
            .Any(form => form.Pedigree == 9 && form.Kind is ChocoboFormKind.Fledgling or ChocoboFormKind.Retired
                && inventoryService.TryReadSlot(form.Container, form.Slot, out var item) && item is { } snapshot
                && ChocoboInventoryForm.TryDecode(snapshot, out var live) && live == form);
    }

    private bool StockCleanupStillValid(StockCleanupPreview preview)
        => preview.ContentId != 0 && preview.ContentId == Plugin.PlayerState.ContentId
           && preview.TerritoryId == Plugin.ClientState.TerritoryType && CanCleanStock()
           && preview.Protection.Matches(StockCleanupProtection.Capture(characterStateService.GetCurrent()));

    private void UpdateStockCleanup()
    {
        var batch = stockCleanup!;
        if (!StockCleanupStillValid(batch)) { StopStockCleanup("Breeding, character or stock changed; cleanup stopped."); return; }
        var expected = batch.Items[stockCleanupIndex];
        if (!inventoryService.TryReadSlot(expected.Container, expected.Slot, out var current))
        { StopStockCleanup("Breeding, character or stock changed; cleanup stopped."); return; }
        if (stockCleanupSentAt != DateTime.MinValue)
        {
            if (current == null)
            {
                stockCleanupRemoved += expected.Quantity;
                stockCleanupIndex++;
                stockCleanupSentAt = DateTime.MinValue;
                if (stockCleanupIndex == batch.Items.Count)
                {
                    stockCleanup = null;
                    StockCleanupStatus = "Stock cleanup completed.";
                }
                return;
            }
            if (current != expected) { StopStockCleanup("Breeding, character or stock changed; cleanup stopped."); return; }
            if ((DateTime.UtcNow - stockCleanupSentAt).TotalSeconds >= 5)
                StopStockCleanup("Discard removal was not observed; cleanup stopped.");
            return;
        }
        if (current != expected || !inventoryService.TryDiscardItem(expected))
        { StopStockCleanup("Breeding, character or stock changed; cleanup stopped."); return; }
        stockCleanupSentAt = DateTime.UtcNow;
    }

    private void StopStockCleanup(string reason)
    {
        stockCleanup = null;
        StockCleanupStatus = reason;
    }
}
