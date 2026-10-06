using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType;

namespace ChokeAbo.Services;

public static class GameHelpers
{
    public const ushort ChocoboSquareTerritoryId = 388;

    private static DateTime lastInteractionAtUtc = DateTime.MinValue;
    private static DateTime lastTravelCommandAtUtc = DateTime.MinValue;
    private static DateTime lastMoveCommandAtUtc = DateTime.MinValue;

    public static bool IsPlayerAvailable()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player == null)
            return false;

        if (player.IsCasting)
            return false;

        if (Plugin.Condition[ConditionFlag.Occupied] ||
            Plugin.Condition[ConditionFlag.OccupiedInQuestEvent] ||
            Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.BetweenAreas] ||
            Plugin.Condition[ConditionFlag.BetweenAreas51] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene])
        {
            return false;
        }

        return true;
    }

    public static IGameObject? FindObjectByName(string name)
    {
        return Plugin.ObjectTable.FirstOrDefault(obj =>
            obj.ObjectKind is DalamudObjectKind.EventNpc or DalamudObjectKind.BattleNpc or DalamudObjectKind.EventObj or DalamudObjectKind.HousingEventObject &&
            obj.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsInChocoboSquare()
        => Plugin.ClientState.TerritoryType == ChocoboSquareTerritoryId;

    public static float GetDistanceTo(string name)
    {
        var obj = FindObjectByName(name);
        var player = Plugin.ObjectTable.LocalPlayer;
        if (obj == null || player == null)
            return float.MaxValue;

        return Vector3.Distance(player.Position, obj.Position);
    }

    public static bool IsNearObject(string name, float? interactionDistance = null)
    {
        var obj = FindObjectByName(name);
        var player = Plugin.ObjectTable.LocalPlayer;
        if (obj == null || player == null)
            return false;

        var maxDistance = interactionDistance ?? GetValidInteractionDistance(obj);
        return Vector3.Distance(player.Position, obj.Position) <= maxDistance;
    }

    public static unsafe bool TargetAndInteract(string name)
    {
        var obj = FindObjectByName(name);
        if (obj == null || !obj.IsTargetable)
            return false;

        if ((DateTime.UtcNow - lastInteractionAtUtc).TotalSeconds < 2.5)
            return false;

        if (!IsPlayerAvailable())
            return false;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null)
        {
            var maxDistance = GetValidInteractionDistance(obj);
            if (Vector3.Distance(player.Position, obj.Position) > maxDistance)
                return false;
        }

        try
        {
            Plugin.TargetManager.Target = obj;
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return false;

            targetSystem->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address, false);
            lastInteractionAtUtc = DateTime.UtcNow;
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"[ChokeAbo] Failed to interact with '{name}': {ex.Message}");
            return false;
        }
    }

    public static unsafe bool IsAddonVisible(string addonName)
    {
        try
        {
            var addon = Instance()->GetAddonByName(addonName);
            return addon != null && addon->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    public static unsafe void FireAddonCallback(string addonName, bool updateState, params object[] args)
    {
        var addon = Instance()->GetAddonByName(addonName);
        if (addon == null || !addon->IsVisible)
            return;

        var atkValues = new AtkValue[args.Length];
        for (var index = 0; index < args.Length; index++)
        {
            atkValues[index] = args[index] switch
            {
                    int intValue => new AtkValue { Type = AtkValueType.Int, Int = intValue },
                    uint uintValue => new AtkValue { Type = AtkValueType.UInt, UInt = uintValue },
                    bool boolValue => new AtkValue { Type = AtkValueType.Bool, Byte = (byte)(boolValue ? 1 : 0) },
                    _ => new AtkValue { Type = AtkValueType.Int, Int = Convert.ToInt32(args[index]) },
            };
        }

        fixed (AtkValue* pointer = atkValues)
        {
            addon->FireCallback((uint)atkValues.Length, pointer, updateState);
        }
    }

    public static string FormatCallbackCommand(string addonName, bool updateState, params object[] args)
    {
        var formattedArgs = args.Length == 0
            ? string.Empty
            : " " + string.Join(" ", args.Select(FormatCallbackArgument));
        return $"/callback {addonName} {(updateState ? "true" : "false")}{formattedArgs}";
    }

    public static bool ClickYesIfVisible()
    {
        if (!IsAddonVisible("SelectYesno"))
            return false;

        FireAddonCallback("SelectYesno", true, 0);
        return true;
    }

    public static unsafe bool TrySelectNativeListEntry(string addonName, string? label, Action beforeDispatch, bool randomize = false, uint? observedNodeId = null, Func<string, bool>? matches = null)
    {
        var handle = Plugin.GameGui.GetAddonByName(addonName);
        if (handle.IsNull || !handle.IsVisible || !handle.IsReady) return false;
        var addon = (AtkUnitBase*)handle.Address;
        if (addon->UldManager.NodeListCount > 512) return false;
        for (var i = 0; i < addon->UldManager.NodeListCount; ++i)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || !node->IsVisible() || (ushort)node->Type < 1000 ||
                observedNodeId.HasValue && node->NodeId != observedNodeId.Value) continue;
            var component = node->GetAsAtkComponentNode()->Component;
            if (component == null || component->GetComponentType() is not (ComponentType.List or ComponentType.TreeList)) continue;
            var list = (AtkComponentList*)component;
            if (list->ListLength is <= 0 or > 512) continue;
            var registered = false;
            var count = 0;
            for (var evt = node->AtkEventManager.Event; evt != null && count++ < 32; evt = evt->NextEvent)
                registered |= evt->State.EventType == AtkEventType.ListItemClick && evt->Listener != null &&
                    !evt->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent);
            if (!registered) continue;
            var selectedRow = -1;
            var selectedLabel = string.Empty;
            var candidates = 0;
            for (var row = 0; row < list->ListLength; ++row)
            {
                var renderer = list->GetItemRenderer(row);
                if (renderer == null || renderer->ListItemIndex != row || !renderer->IsEnabled || renderer->ButtonTextNode == null) continue;
                var text = renderer->ButtonTextNode->NodeText.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text) || label != null && text != label || matches != null && !matches(text)) continue;
                if (!randomize || Random.Shared.Next(++candidates) == 0)
                { selectedRow = row; selectedLabel = text; }
                if (!randomize) break;
            }
            if (selectedRow < 0) continue;
            beforeDispatch();
            Plugin.Log.Information($"[ChokeAbo][Native] Selecting registered list action: addon={addonName}; node={node->NodeId}; row={selectedRow}; text={selectedLabel}");
            list->DispatchItemEvent(selectedRow, AtkEventType.ListItemClick);
            return true;
        }
        return false;
    }

    public static unsafe bool TryClickNativeButton(string addonName, string? label, uint? observedNodeId = null)
    {
        var handle = Plugin.GameGui.GetAddonByName(addonName);
        if (handle.IsNull || !handle.IsVisible || !handle.IsReady) return false;
        var addon = (AtkUnitBase*)handle.Address;
        if (addon->UldManager.NodeListCount > 512) return false;
        AtkEvent* selected = null;
        uint selectedNode = 0;
        for (var i = 0; i < addon->UldManager.NodeListCount; ++i)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || !node->IsVisible() || (ushort)node->Type < 1000 ||
                observedNodeId.HasValue && node->NodeId != observedNodeId.Value) continue;
            var component = node->GetAsAtkComponentNode()->Component;
            if (component == null || component->GetComponentType() is not (ComponentType.Button or ComponentType.RadioButton)) continue;
            var button = (AtkComponentButton*)component;
            if (!button->IsEnabled || label != null &&
                (button->ButtonTextNode == null || button->ButtonTextNode->NodeText.ToString().Trim() != label)) continue;
            var count = 0;
            for (var evt = node->AtkEventManager.Event; evt != null && count++ < 32; evt = evt->NextEvent)
            {
                if (evt->State.EventType != AtkEventType.ButtonClick || evt->Listener != (AtkEventListener*)addon ||
                    evt->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent)) continue;
                if (selected != null) return false;
                selected = evt;
                selectedNode = node->NodeId;
            }
        }
        if (selected == null) return false;
        var click = *selected;
        var data = new AtkEventData();
        Plugin.Log.Information($"[ChokeAbo][Native] Clicking registered button: addon={addonName}; node={selectedNode}; param={click.Param}; label={label}");
        click.Listener->ReceiveEvent(AtkEventType.ButtonClick, checked((int)click.Param), &click, &data);
        return true;
    }

    public static bool TryTravelToChocoboSquare()
    {
        if (Plugin.Condition[ConditionFlag.OccupiedInEvent]) return false;
        if ((DateTime.UtcNow - lastTravelCommandAtUtc).TotalSeconds < 4)
            return false;

        if (TryLifestreamCommand("chocobo"))
        {
            lastTravelCommandAtUtc = DateTime.UtcNow;
            return true;
        }

        if (SendGameCommand("/li chocobo"))
        {
            lastTravelCommandAtUtc = DateTime.UtcNow;
            return true;
        }

        return false;
    }

    public static unsafe bool TryReadMgpShopEntry(int row, out uint itemId, out uint price)
    {
        itemId = price = 0;
        var handle = Plugin.GameGui.GetAddonByName("ShopExchangeCurrency");
        if (handle.IsNull || !handle.IsVisible || !handle.IsReady || handle.AtkValuesCount < 1188 || row is < 0 or >= 122) return false;
        var addon = (AtkUnitBase*)handle.Address;
        // Captured Race Items layout: currency at 3, row count at 4,
        // prices at 456 and item IDs at 1066 (122 entries per column).
        if (addon->AtkValues[3].Type != AtkValueType.UInt || addon->AtkValues[3].UInt != 29 ||
            addon->AtkValues[4].Type != AtkValueType.UInt || addon->AtkValues[4].UInt > 122 || row >= addon->AtkValues[4].UInt ||
            addon->AtkValues[456 + row].Type != AtkValueType.UInt || addon->AtkValues[1066 + row].Type != AtkValueType.UInt) return false;
        itemId = addon->AtkValues[1066 + row].UInt;
        price = addon->AtkValues[456 + row].UInt;
        return itemId != 0 && price != 0;
    }

    private static unsafe FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler* GetGilShopHandler()
    {
        var handle = Plugin.GameGui.GetAddonByName("Shop");
        if (handle.IsNull || !handle.IsVisible || !handle.IsReady) return null;
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentShop.Instance();
        var proxy = FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler.AgentProxy.Instance();
        if (agent == null || proxy == null || (nint)agent->EventReceiver != (nint)proxy ||
            proxy->AddonId != ((AtkUnitBase*)handle.Address)->Id || proxy->Handler == null) return null;
        var shop = proxy->Handler;
        return !shop->BuybackTabActive && shop->ItemsCount is > 0 and <= 60 &&
            shop->VisibleItemsCount is > 0 and <= 60 ? shop : null;
    }

    public static unsafe bool TryReadGilShopEntry(int row, out uint itemId, out uint price)
    {
        itemId = price = 0;
        var shop = GetGilShopHandler();
        if (shop == null || row < 0 || row >= shop->VisibleItemsCount) return false;
        var index = shop->VisibleItems[row];
        if (index < 0 || index >= shop->ItemsCount) return false;
        var item = shop->Items[index];
        if (item.ItemId == 0 || item.PriceBuy <= 0) return false;
        itemId = item.ItemId;
        price = (uint)item.PriceBuy;
        return true;
    }

    public static unsafe bool TryBuyGilShopEntry(int row, uint itemId, uint price)
    {
        if (!TryReadGilShopEntry(row, out var liveItem, out var livePrice) ||
            liveItem != itemId || livePrice != price || IsAddonVisible("SelectYesno")) return false;
        var shop = GetGilShopHandler();
        if (shop == null || shop->StartingBuy || shop->WaitingForTransactionToFinish) return false;
        // The captured AgentShop proxy maps visible rows to actual ShopItems.
        // ClientStructs requires this native index before ExecuteBuy(count).
        shop->BuyItemIndex = shop->VisibleItems[row];
        shop->ExecuteBuy(1);
        return true;
    }

    public static unsafe bool TryCloseFeathertraderShop()
    {
        if (Plugin.TargetManager.Target?.BaseId != 1011585 || IsAddonVisible("SelectYesno")) return false;
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentShop.Instance();
        var proxy = FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler.AgentProxy.Instance();
        if (agent == null) return false;
        var closeDispatched = false;
        if (proxy != null && proxy->Handler != null && Plugin.Condition[ConditionFlag.OccupiedInEvent])
        {
            var handler = proxy->Handler;
            if (handler->ItemsCount == 7 && handler->Items[5].ItemId == ChocoboInventoryModel.FirstFledglingItemId &&
                handler->Items[6].ItemId == ChocoboInventoryModel.FirstFemaleFledglingItemId &&
                !handler->StartingBuy && !handler->WaitingForTransactionToFinish)
            {
                foreach (var obj in handler->EventObjects)
                {
                    if (obj.Value == null || (ulong)obj.Value->GetGameObjectId() != Plugin.TargetManager.Target.GameObjectId) continue;
                    Plugin.Log.Information($"[ChokeAbo][Native] Cancelling owned Feathertrader interaction through its native ShopEventHandler; event={handler->Info.EventId.Id:X}.");
                    handler->CancelInteraction();
                    closeDispatched = true;
                    break;
                }
            }
        }
        if (agent->IsAgentActive())
        {
            if (proxy == null || (nint)agent->EventReceiver != (nint)proxy || proxy->Handler == null) return false;
            var handler = proxy->Handler;
            if (handler->ItemsCount != 7 || handler->Items[5].ItemId != ChocoboInventoryModel.FirstFledglingItemId ||
                handler->Items[6].ItemId != ChocoboInventoryModel.FirstFemaleFledglingItemId || handler->StartingBuy || handler->WaitingForTransactionToFinish) return false;
            Plugin.Log.Information("[ChokeAbo][Native] Closing owned Feathertrader shop through AgentShop.Hide.");
            agent->Hide();
            closeDispatched = true;
        }
        // The captured vendor inventory remains OpenType5/title1 after Shop is
        // gone. Its agent must also close before OccupiedInEvent is released.
        var inventory = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventory.Instance();
        if (inventory != null && inventory->IsAgentActive() && inventory->OpenType == 5 && inventory->OpenTitleId == 1 &&
            inventory->CurrentInventoryContextEvent == null)
        {
            Plugin.Log.Information("[ChokeAbo][Native] Closing owned Feathertrader inventory through AgentInventory.Hide.");
            inventory->Hide();
            closeDispatched = true;
        }
        return closeDispatched || !agent->IsAgentActive() && !Plugin.Condition[ConditionFlag.OccupiedInEvent];
    }

    public static unsafe bool TryClosePermitShop(uint permitItemId)
    {
        if (permitItemId is < ChocoboInventoryModel.FirstCoveringPermissionItemId or > ChocoboInventoryModel.LastCoveringPermissionItemId ||
            IsAddonVisible("SelectYesno")) return false;
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentShop.Instance();
        if (agent == null) return false;
        var handle = Plugin.GameGui.GetAddonByName("ShopExchangeCurrency");
        if (handle.IsNull || !handle.IsVisible)
            return !agent->IsAgentActive() && !Plugin.Condition[ConditionFlag.OccupiedInEvent];
        // Resume cleanup may clear the NPC target after the shop has already closed.
        // A visible shop still requires its actual supplier and addon ownership.
        if (Plugin.TargetManager.Target?.BaseId != 1010488 || !handle.IsReady ||
            !agent->IsAgentActive() || agent->AddonId != ((AtkUnitBase*)handle.Address)->Id) return false;
        for (var row = 0; row < 122; ++row)
        {
            if (!TryReadMgpShopEntry(row, out var itemId, out _) || itemId != permitItemId) continue;
            // Close the actual addon and release its verified shop agent. Each
            // operation alone has left part of this supplier interaction active.
            Plugin.Log.Information($"[ChokeAbo][Native] Closing verified permit addon and shop agent; item={permitItemId}; addon={agent->AddonId}.");
            ((AtkUnitBase*)handle.Address)->Close(true);
            agent->Hide();
            return true;
        }
        return false;
    }

    public static unsafe bool IsMgpPurchaseConfirmation(int row, uint itemId, uint price, uint quantity)
    {
        if (quantity == 0 || !TryReadMgpShopEntry(row, out var liveItem, out var livePrice) ||
            liveItem != itemId || livePrice != price) return false;
        var shop = Plugin.GameGui.GetAddonByName("ShopExchangeCurrency");
        var confirm = Plugin.GameGui.GetAddonByName("SelectYesno");
        if (confirm.IsNull || !confirm.IsVisible || !confirm.IsReady || confirm.AtkValuesCount < 18) return false;
        var dialog = (AtkUnitBase*)confirm.Address;
        if (dialog->BlockedParentId != ((AtkUnitBase*)shop.Address)->Id ||
            dialog->AtkValues[14].Type != AtkValueType.UInt || dialog->AtkValues[14].UInt != itemId ||
            dialog->AtkValues[17].Type != AtkValueType.UInt || dialog->AtkValues[17].UInt != quantity) return false;
        var promptNode = dialog->GetTextNodeById(2);
        if (promptNode == null) return false;
        var prompt = Dalamud.Game.Text.SeStringHandling.SeString.Parse(promptNode->NodeText.AsSpan()).TextValue;
        var cost = checked((ulong)price * quantity);
        return prompt == $"Exchange {cost:N0} MGP for the following item?" ||
            prompt == $"Exchange {cost} MGP for the following item?";
    }

    public static bool IsLifestreamBusy()
    {
        try
        {
            return Plugin.PluginInterface
                .GetIpcSubscriber<bool>("Lifestream.IsBusy")
                .InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    public static bool TryMoveCloseTo(string name, float stopDistance)
    {
        var obj = FindObjectByName(name);
        if (obj == null)
            return false;

        return TryMoveCloseTo(obj.Position, stopDistance);
    }

    public static bool TryMoveCloseTo(Vector3 position, float stopDistance)
    {
        if ((DateTime.UtcNow - lastMoveCommandAtUtc).TotalSeconds < 2)
            return false;

        try
        {
            var navReady = Plugin.PluginInterface
                .GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady")
                .InvokeFunc();
            if (!navReady)
                return false;

            var started = Plugin.PluginInterface
                .GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo")
                .InvokeFunc(position, false, stopDistance);
            if (started)
                lastMoveCommandAtUtc = DateTime.UtcNow;

            return started;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsMovementRunning()
    {
        try
        {
            var pathRunning = Plugin.PluginInterface
                .GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning")
                .InvokeFunc();
            if (pathRunning)
                return true;

            var pathfindRunning = Plugin.PluginInterface
                .GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress")
                .InvokeFunc();
            return pathfindRunning;
        }
        catch
        {
            return false;
        }
    }

    public static void StopMovement()
    {
        try
        {
            Plugin.PluginInterface
                .GetIpcSubscriber<object>("vnavmesh.Path.Stop")
                .InvokeAction();
        }
        catch
        {
        }
    }

    public static unsafe bool SendGameCommand(string command)
    {
        try
        {
            if (Plugin.CommandManager.ProcessCommand(command))
                return true;

            var uiModule = UIModule.Instance();
            if (uiModule == null)
                return false;

            var bytes = System.Text.Encoding.UTF8.GetBytes(command);
            var utf8 = Utf8String.FromSequence(bytes);
            uiModule->ProcessChatBoxEntry(utf8, nint.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLifestreamCommand(string command)
    {
        try
        {
            Plugin.PluginInterface
                .GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand")
                .InvokeAction(command);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static float GetValidInteractionDistance(IGameObject obj)
        => obj.ObjectKind switch
        {
            DalamudObjectKind.EventNpc => 4.0f,
            DalamudObjectKind.BattleNpc => 3.0f,
            DalamudObjectKind.EventObj => 2.0f,
            DalamudObjectKind.HousingEventObject => 2.0f,
            _ => 2.5f,
        };

    private static string FormatCallbackArgument(object value)
        => value switch
        {
            bool boolValue => boolValue ? "1" : "0",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
}
