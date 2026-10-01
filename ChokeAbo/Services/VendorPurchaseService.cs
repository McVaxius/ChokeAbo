using Dalamud.Plugin.Services;

namespace ChokeAbo.Services;

public sealed class VendorPurchaseService
{
    private const string VendorName = "Tack & Feed Trader";
    private const float VendorStopDistance = 3.2f;

    private readonly InventoryService inventoryService;
    private readonly IPluginLog log;

    private VendorPurchaseState state = VendorPurchaseState.Idle;
    private IReadOnlyList<FeedPurchaseEntry> queue = Array.Empty<FeedPurchaseEntry>();
    private int currentIndex;
    private DateTime stateEnteredAtUtc = DateTime.MinValue;
    private DateTime lastInteractionAtUtc = DateTime.MinValue;
    private DateTime lastActionAtUtc = DateTime.MinValue;
    private int categoryAttempts;
    private int purchaseAttempts;
    private uint lastObservedItemCount;
    private string lastError = string.Empty;
    private string lastCallbackCommand = string.Empty;
    private uint gilReserve;
    private uint mgpReserve;
    private bool verifyShopEntry;
    private bool confirmationDispatched;

    private enum VendorPurchaseState
    {
        Idle,
        TravelingToChocoboSquare,
        NavigatingToVendor,
        InteractingVendor,
        SelectingCategory,
        Purchasing,
        WaitingForPurchaseResolution,
        Complete,
        Failed,
    }

    public VendorPurchaseService(InventoryService inventoryService, IPluginLog log)
    {
        this.inventoryService = inventoryService;
        this.log = log;
    }

    public string Summary => "Automates the Tack & Feed Trader shopping loop with travel, staging, and stop-safe recovery.";
    public bool IsRunning => state is not VendorPurchaseState.Idle and not VendorPurchaseState.Complete and not VendorPurchaseState.Failed;
    public bool IsComplete => state == VendorPurchaseState.Complete;
    public bool IsFailed => state == VendorPurchaseState.Failed;
    public bool PurchaseWasDispatched => purchaseAttempts > 0;
    public bool ReserveBlocked { get; private set; }
    public string StatusText => state switch
    {
        VendorPurchaseState.Idle => "Idle",
        VendorPurchaseState.TravelingToChocoboSquare => "Traveling to Chocobo Square...",
        VendorPurchaseState.NavigatingToVendor => "Moving to Tack & Feed Trader...",
        VendorPurchaseState.InteractingVendor => "Opening Tack & Feed Trader...",
        VendorPurchaseState.SelectingCategory => "Selecting vendor page...",
        VendorPurchaseState.Purchasing => CurrentEntry is { } entry ? $"Buying {entry.FeedName}..." : "Buying feed...",
        VendorPurchaseState.WaitingForPurchaseResolution => "Waiting for purchase result...",
        VendorPurchaseState.Complete => "Purchase cycle complete.",
        VendorPurchaseState.Failed => string.IsNullOrWhiteSpace(lastError) ? "Purchase cycle failed." : lastError,
        _ => state.ToString(),
    };

    private FeedPurchaseEntry? CurrentEntry => currentIndex >= 0 && currentIndex < queue.Count ? queue[currentIndex] : null;

    public void Start(FeedPurchasePlan plan, uint gilReserve = 0, uint mgpReserve = 0, bool verifyShopEntry = false, bool useOpenShop = false)
    {
        this.gilReserve = gilReserve;
        this.mgpReserve = mgpReserve;
        queue = plan.Entries
            .Where(entry => entry.QuantityToBuy > 0)
            .OrderBy(entry => entry.CurrencyKind)
            .ThenBy(entry => entry.VendorCallbackIndex)
            .ToArray();

        ResetRuntimeFields();
        this.verifyShopEntry = verifyShopEntry;
        if (queue.Count == 0)
        {
            state = VendorPurchaseState.Complete;
            return;
        }

        log.Information($"[ChokeAbo] Starting buy cycle for {queue.Count} feed entries.");
        SetState(useOpenShop ? VendorPurchaseState.Purchasing : GameHelpers.IsInChocoboSquare()
            ? VendorPurchaseState.NavigatingToVendor
            : VendorPurchaseState.TravelingToChocoboSquare);
    }

    public bool AdoptPendingRequiredPurchase(FeedPurchasePlan plan, uint gilReserve, uint mgpReserve)
    {
        if (plan.Entries.Count != 1) return false;
        var entry = plan.Entries[0];
        if (entry.QuantityToBuy != 1 || inventoryService.GetItemCount(entry.ItemId) != entry.OnHandQuantity ||
            !GameHelpers.IsMgpPurchaseConfirmation(entry.VendorCallbackIndex, entry.ItemId, entry.UnitCost, 1)) return false;
        Start(plan, gilReserve, mgpReserve, verifyShopEntry: true);
        purchaseAttempts = 1;
        lastObservedItemCount = entry.OnHandQuantity;
        SetState(VendorPurchaseState.WaitingForPurchaseResolution);
        log.Information($"[ChokeAbo] Adopted pending required-supply confirmation for item {entry.ItemId}; no purchase callback replayed.");
        return true;
    }

    public void Reset()
    {
        GameHelpers.StopMovement();
        queue = Array.Empty<FeedPurchaseEntry>();
        ResetRuntimeFields();
        state = VendorPurchaseState.Idle;
        stateEnteredAtUtc = DateTime.MinValue;
    }

    public void Update()
    {
        if (state is VendorPurchaseState.Idle or VendorPurchaseState.Complete or VendorPurchaseState.Failed)
            return;

        if (state == VendorPurchaseState.WaitingForPurchaseResolution && !confirmationDispatched && !PurchaseWithinReserves())
        {
            // A confirmed inventory increase resolves before checking another purchase.
            if (CurrentEntry is { } pending && TryAdvanceIfCurrentEntryComplete(pending)) return;
            ReserveBlocked = true;
            Fail("Feed confirmation would cross the configured currency reserve.");
            return;
        }
        if (state == VendorPurchaseState.WaitingForPurchaseResolution && !verifyShopEntry && GameHelpers.ClickYesIfVisible())
            return;

        if (CurrentEntry == null)
        {
            Complete();
            return;
        }

        switch (state)
        {
            case VendorPurchaseState.TravelingToChocoboSquare:
                HandleTravelingToChocoboSquare();
                break;
            case VendorPurchaseState.NavigatingToVendor:
                HandleNavigatingToVendor();
                break;
            case VendorPurchaseState.InteractingVendor:
                HandleInteractingVendor();
                break;
            case VendorPurchaseState.SelectingCategory:
                HandleSelectingCategory();
                break;
            case VendorPurchaseState.Purchasing:
                HandlePurchasing();
                break;
            case VendorPurchaseState.WaitingForPurchaseResolution:
                HandleWaitingForPurchaseResolution();
                break;
        }
    }

    private void HandleTravelingToChocoboSquare()
    {
        if (GameHelpers.IsLifestreamBusy() || !GameHelpers.IsPlayerAvailable()) return;
        if (GameHelpers.IsInChocoboSquare())
        {
            SetState(VendorPurchaseState.NavigatingToVendor);
            return;
        }

        if (lastInteractionAtUtc < stateEnteredAtUtc)
        {
            if (!GameHelpers.TryTravelToChocoboSquare())
            {
                if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 10)
                    Fail("Could not start Lifestream travel to Chocobo Square.");
                return;
            }
            lastInteractionAtUtc = DateTime.UtcNow;
            log.Information("[ChokeAbo] Traveling to Chocobo Square.");
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 45)
            Fail("Timed out waiting to arrive in Chocobo Square.");
    }

    private void HandleNavigatingToVendor()
    {
        if (!GameHelpers.IsInChocoboSquare())
        {
            SetState(VendorPurchaseState.TravelingToChocoboSquare);
            return;
        }

        if (GameHelpers.IsNearObject(VendorName))
        {
            GameHelpers.StopMovement();
            SetState(VendorPurchaseState.InteractingVendor);
            return;
        }

        if (GameHelpers.FindObjectByName(VendorName) == null)
        {
            if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 8)
                Fail("Could not find Tack & Feed Trader in Chocobo Square.");
            return;
        }

        if (!GameHelpers.IsMovementRunning() &&
            !GameHelpers.TryMoveCloseTo(VendorName, VendorStopDistance) &&
            (DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 10)
        {
            Fail("Could not path near Tack & Feed Trader. Move closer or check vnavmesh.");
            return;
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 20)
            Fail("Timed out moving near Tack & Feed Trader.");
    }

    private void HandleInteractingVendor()
    {
        var current = CurrentEntry;
        if (current == null)
        {
            Complete();
            return;
        }

        if (!GameHelpers.IsNearObject(VendorName))
        {
            SetState(VendorPurchaseState.NavigatingToVendor);
            return;
        }

        if (GameHelpers.IsAddonVisible(current.ExpectedAddonName))
        {
            categoryAttempts = 0;
            purchaseAttempts = 0;
            SetState(VendorPurchaseState.Purchasing);
            return;
        }

        if (GameHelpers.IsAddonVisible("SelectString") || GameHelpers.IsAddonVisible("SelectIconString"))
        {
            categoryAttempts = 0;
            SetState(VendorPurchaseState.SelectingCategory);
            return;
        }

        if ((DateTime.UtcNow - lastInteractionAtUtc).TotalSeconds >= 2.5)
        {
            lastInteractionAtUtc = DateTime.UtcNow;
            if (GameHelpers.TargetAndInteract(VendorName))
                return;

            if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 12)
                Fail("Could not open Tack & Feed Trader. Move closer and try again.");
        }
    }

    private void HandleSelectingCategory()
    {
        var current = CurrentEntry;
        if (current == null)
        {
            Complete();
            return;
        }

        if (GameHelpers.IsAddonVisible(current.ExpectedAddonName))
        {
            SetState(VendorPurchaseState.Purchasing);
            return;
        }

        if (!GameHelpers.IsAddonVisible("SelectString") && !GameHelpers.IsAddonVisible("SelectIconString"))
        {
            SetState(VendorPurchaseState.InteractingVendor);
            return;
        }

        if ((DateTime.UtcNow - lastActionAtUtc).TotalSeconds < 0.8)
            return;

        if (categoryAttempts >= 2)
        {
            Fail($"Could not open '{current.VendorCategoryLabel}' on Tack & Feed Trader.");
            return;
        }

        var menuAddon = GameHelpers.IsAddonVisible("SelectString") ? "SelectString" : "SelectIconString";
        var index = current.VendorCategoryIndex + categoryAttempts;
        lastActionAtUtc = DateTime.UtcNow;
        categoryAttempts++;
        lastCallbackCommand = GameHelpers.FormatCallbackCommand(menuAddon, true, index);
        log.Information($"[ChokeAbo] Selecting '{current.VendorCategoryLabel}' via {lastCallbackCommand}");
        GameHelpers.FireAddonCallback(menuAddon, true, index);
    }

    private void HandlePurchasing()
    {
        var current = CurrentEntry;
        if (current == null)
        {
            Complete();
            return;
        }

        if (TryAdvanceIfCurrentEntryComplete(current))
            return;

        if (!GameHelpers.IsAddonVisible(current.ExpectedAddonName))
        {
            if (verifyShopEntry) { Fail("The verified required-supply shop closed before purchase."); return; }
            SetState(VendorPurchaseState.InteractingVendor);
            return;
        }

        if ((DateTime.UtcNow - lastActionAtUtc).TotalSeconds < 0.8)
            return;

        var onHand = inventoryService.GetItemCount(current.ItemId);
        lastObservedItemCount = onHand;
        lastActionAtUtc = DateTime.UtcNow;
        var remaining = Math.Max(1, current.PlannedQuantity - (int)onHand);
        if (!PurchaseWithinReserves())
        {
            ReserveBlocked = true;
            Fail("Feed purchase would cross the configured currency reserve.");
            return;
        }
        if (string.Equals(current.ExpectedAddonName, "Shop", StringComparison.Ordinal))
        {
            if (verifyShopEntry)
            {
                // Required G1 forms use the captured native shop handler, not feed callback positions.
                if (!GameHelpers.TryBuyGilShopEntry(current.VendorCallbackIndex, current.ItemId, current.UnitCost))
                { Fail("The native gil-shop purchase could not be dispatched safely."); return; }
                purchaseAttempts++;
                lastCallbackCommand = $"ShopEventHandler.ExecuteBuy(1), visible row {current.VendorCallbackIndex}";
                log.Information($"[ChokeAbo] Buying 1 x {current.FeedName} via {lastCallbackCommand}");
                SetState(VendorPurchaseState.WaitingForPurchaseResolution);
                return;
            }
            purchaseAttempts++;
            lastCallbackCommand = GameHelpers.FormatCallbackCommand("Shop", true, current.VendorCallbackGroup, current.VendorCallbackIndex);
            log.Information($"[ChokeAbo] Buying 1 x {current.FeedName} from gil shop callback slot {current.VendorCallbackIndex} via {lastCallbackCommand}");
            GameHelpers.FireAddonCallback("Shop", true, current.VendorCallbackGroup, current.VendorCallbackIndex);
            SetState(VendorPurchaseState.WaitingForPurchaseResolution);
            return;
        }

        var quantity = Math.Clamp(remaining, 1, 99);
        purchaseAttempts++;
        lastCallbackCommand = GameHelpers.FormatCallbackCommand("ShopExchangeCurrency", true, current.VendorCallbackGroup, current.VendorCallbackIndex, quantity);
        log.Information($"[ChokeAbo] Buying {quantity} x {current.FeedName} from MGP shop callback slot {current.VendorCallbackIndex} via {lastCallbackCommand}");
        GameHelpers.FireAddonCallback("ShopExchangeCurrency", true, current.VendorCallbackGroup, current.VendorCallbackIndex, quantity);
        SetState(VendorPurchaseState.WaitingForPurchaseResolution);
    }

    private void HandleWaitingForPurchaseResolution()
    {
        var current = CurrentEntry;
        if (current == null)
        {
            Complete();
            return;
        }

        if (TryAdvanceIfCurrentEntryComplete(current))
            return;

        var onHand = inventoryService.GetItemCount(current.ItemId);
        if (verifyShopEntry && current.CurrencyKind == FeedCurrencyKind.Mgp && !confirmationDispatched &&
            GameHelpers.IsMgpPurchaseConfirmation(current.VendorCallbackIndex, current.ItemId, current.UnitCost, 1) &&
            PurchaseWithinReserves() && GameHelpers.TryClickNativeButton("SelectYesno", "Yes", 8))
        {
            confirmationDispatched = true;
            stateEnteredAtUtc = DateTime.UtcNow;
            return;
        }
        if (onHand > lastObservedItemCount)
        {
            purchaseAttempts = 0;
            SetState(VendorPurchaseState.Purchasing);
            return;
        }

        if (!GameHelpers.IsAddonVisible(current.ExpectedAddonName) &&
            (DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds > 1.0)
        {
            if (verifyShopEntry) { Fail("Required-supply shop closed before the purchase result was verified."); return; }
            SetState(VendorPurchaseState.InteractingVendor);
            return;
        }

        if ((DateTime.UtcNow - stateEnteredAtUtc).TotalSeconds <= 4.0)
            return;

        if (purchaseAttempts >= (verifyShopEntry ? 1 : 2))
        {
            Fail($"Purchase callback for {current.FeedName} did not change inventory. Last attempted: {lastCallbackCommand}");
            return;
        }

        SetState(VendorPurchaseState.Purchasing);
    }

    private bool PurchaseWithinReserves()
    {
        if (CurrentEntry is not { } entry) return false;
        if (verifyShopEntry)
        {
            var mapped = entry.CurrencyKind == FeedCurrencyKind.Gil
                ? GameHelpers.TryReadGilShopEntry(entry.VendorCallbackIndex, out var itemId, out var price)
                : GameHelpers.TryReadMgpShopEntry(entry.VendorCallbackIndex, out itemId, out price);
            if (!mapped || itemId != entry.ItemId || price != entry.UnitCost) return false;
        }
        var remaining = Math.Max(0, entry.PlannedQuantity - (int)inventoryService.GetItemCount(entry.ItemId));
        var quantity = entry.CurrencyKind == FeedCurrencyKind.Gil ? Math.Min(1, remaining) : Math.Min(99, remaining);
        var cost = checked(entry.UnitCost * (uint)quantity);
        return BreedingService.CanSpend(inventoryService.GetItemCount(entry.CurrencyKind == FeedCurrencyKind.Gil ? 1u : 29u),
            cost, entry.CurrencyKind == FeedCurrencyKind.Gil ? gilReserve : mgpReserve);
    }

    private bool TryAdvanceIfCurrentEntryComplete(FeedPurchaseEntry current)
    {
        var onHand = inventoryService.GetItemCount(current.ItemId);
        if (onHand < current.PlannedQuantity)
            return false;

        log.Information($"[ChokeAbo] Purchase target reached for {current.FeedName}: {onHand}/{current.PlannedQuantity}");
        currentIndex++;
        categoryAttempts = 0;
        purchaseAttempts = 0;
        lastObservedItemCount = 0;

        if (CurrentEntry == null)
        {
            Complete();
        }
        else
        {
            SetState(VendorPurchaseState.InteractingVendor);
        }

        return true;
    }

    private void Complete()
    {
        GameHelpers.StopMovement();
        SetState(VendorPurchaseState.Complete);
    }

    private void SetState(VendorPurchaseState newState)
    {
        state = newState;
        stateEnteredAtUtc = DateTime.UtcNow;
    }

    private void Fail(string message)
    {
        GameHelpers.StopMovement();
        lastError = message;
        log.Warning($"[ChokeAbo] {message}");
        SetState(VendorPurchaseState.Failed);
    }

    private void ResetRuntimeFields()
    {
        currentIndex = 0;
        categoryAttempts = 0;
        purchaseAttempts = 0;
        lastObservedItemCount = 0;
        lastActionAtUtc = DateTime.MinValue;
        lastInteractionAtUtc = DateTime.MinValue;
        lastError = string.Empty;
        lastCallbackCommand = string.Empty;
        ReserveBlocked = false;
        verifyShopEntry = false;
        confirmationDispatched = false;
    }
}
