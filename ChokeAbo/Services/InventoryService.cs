using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace ChokeAbo.Services;

public interface InventoryService
{
    string Summary { get; }
    uint ResolveItemId(string itemName);
    string ResolveItemName(uint itemId);
    uint GetItemCount(uint itemId);
    (InventoryType container, int slot)? FindItemSlot(uint itemId);
    IReadOnlyList<InventoryItemSnapshot> EnumerateItemsInRange(uint firstItemId, uint lastItemId);
    InventoryItemSnapshot? FindItem(InventoryType container, int slot);
    int GetFreeMainInventorySlotCount();
}

public readonly record struct InventoryItemSnapshot(
    uint ItemId,
    string ItemName,
    uint Condition,
    uint Quantity,
    InventoryType Container,
    int Slot);

public sealed class GameInventoryService : InventoryService
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<string, uint> itemIdCache = new(StringComparer.OrdinalIgnoreCase);

    public GameInventoryService(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    public string Summary => "Inventory counts plus deterministic enumeration of every matching item and slot.";

    public uint ResolveItemId(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return 0;

        if (itemIdCache.TryGetValue(itemName, out var cached))
            return cached;

        try
        {
            var itemSheet = dataManager.GetExcelSheet<Item>();
            if (itemSheet == null)
                return 0;

            foreach (var item in itemSheet)
            {
                if (string.Equals(item.Name.ToString(), itemName, StringComparison.OrdinalIgnoreCase))
                {
                    itemIdCache[itemName] = item.RowId;
                    return item.RowId;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to resolve item '{itemName}': {ex.Message}");
        }

        itemIdCache[itemName] = 0;
        return 0;
    }

    public unsafe uint GetItemCount(uint itemId)
    {
        if (itemId == 0)
            return 0;

        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return 0;

            return (uint)Math.Max(0, manager->GetInventoryItemCount(itemId) + manager->GetInventoryItemCount(itemId, true));
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to read item count for {itemId}: {ex.Message}");
            return 0;
        }
    }

    public unsafe (InventoryType container, int slot)? FindItemSlot(uint itemId)
    {
        if (itemId == 0)
            return null;

        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return null;

            var containers = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4,
            };

            foreach (var containerType in containers)
            {
                var container = manager->GetInventoryContainer(containerType);
                if (container == null)
                    continue;

                for (var index = 0; index < container->Size; index++)
                {
                    var slot = container->GetInventorySlot(index);
                    if (slot != null && slot->ItemId == itemId)
                        return (containerType, index);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to find item slot for {itemId}: {ex.Message}");
        }

        return null;
    }

    public unsafe IReadOnlyList<InventoryItemSnapshot> EnumerateItemsInRange(uint firstItemId, uint lastItemId)
    {
        var result = new List<InventoryItemSnapshot>();
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return result;

            var containers = new[]
            {
                InventoryType.Inventory1,
                InventoryType.Inventory2,
                InventoryType.Inventory3,
                InventoryType.Inventory4,
            };

            foreach (var containerType in containers)
            {
                var container = manager->GetInventoryContainer(containerType);
                if (container == null)
                    continue;

                for (var index = 0; index < container->Size; index++)
                {
                    var slot = container->GetInventorySlot(index);
                    if (slot == null || slot->ItemId < firstItemId || slot->ItemId > lastItemId)
                        continue;

                    result.Add(new InventoryItemSnapshot(
                        slot->ItemId,
                        ResolveItemName(slot->ItemId),
                        (uint)slot->Condition,
                        (uint)Math.Max(0, slot->Quantity),
                        containerType,
                        index));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to scan item range {firstItemId}-{lastItemId}: {ex.Message}");
        }

        return result;
    }

    public unsafe InventoryItemSnapshot? FindItem(InventoryType containerType, int slotIndex)
    {
        try
        {
            var manager = InventoryManager.Instance();
            var container = manager == null ? null : manager->GetInventoryContainer(containerType);
            if (container == null || slotIndex < 0 || slotIndex >= container->Size)
                return null;

            var slot = container->GetInventorySlot(slotIndex);
            if (slot == null || slot->ItemId == 0)
                return null;

            return new InventoryItemSnapshot(
                slot->ItemId,
                ResolveItemName(slot->ItemId),
                (uint)slot->Condition,
                (uint)Math.Max(0, slot->Quantity),
                containerType,
                slotIndex);
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to read {containerType} slot {slotIndex}: {ex.Message}");
            return null;
        }
    }

    public unsafe int GetFreeMainInventorySlotCount()
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return 0;

            var free = 0;
            foreach (var containerType in MainInventoryContainers)
            {
                var container = manager->GetInventoryContainer(containerType);
                if (container == null)
                    return 0;

                for (var index = 0; index < container->Size; index++)
                {
                    var slot = container->GetInventorySlot(index);
                    if (slot == null || slot->ItemId == 0)
                        free++;
                }
            }

            return free;
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo] Failed to count free inventory slots: {ex.Message}");
            return 0;
        }
    }

    public string ResolveItemName(uint itemId)
    {
        try
        {
            return dataManager.GetExcelSheet<Item>()?.GetRow(itemId).Name.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static readonly InventoryType[] MainInventoryContainers =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };
}
