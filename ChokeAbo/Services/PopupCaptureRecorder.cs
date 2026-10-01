using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ChokeAbo.Services;

public enum PopupCaptureKind
{
    Retirement,
    CoveringSelector,
    FledglingSelector,
    Adoption,
}

public sealed unsafe class PopupCaptureRecorder : IDisposable
{
    private static readonly AddonEvent[] LifecycleEvents =
    {
        AddonEvent.PostSetup,
        AddonEvent.PostRefresh,
        AddonEvent.PostShow,
        AddonEvent.PreReceiveEvent,
        AddonEvent.PostReceiveEvent,
        AddonEvent.PreFinalize,
    };

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameInventory gameInventory;
    private readonly InventoryService inventoryService;
    private readonly ChocoboStatsService chocoboStatsService;
    private readonly IPluginLog log;
    private readonly Func<bool> isAutomationRunning;
    private readonly string configDirectory;
    private readonly Dictionary<string, string> lastAddonSnapshot = new(StringComparer.Ordinal);
    private IReadOnlyList<ChocoboInventoryForm> startingInventory = Array.Empty<ChocoboInventoryForm>();
    private StreamWriter? writer;
    private readonly Queue<ChocoboInventoryForm> formInspectionQueue = new();
    private ChocoboInventoryForm? inspectingForm;
    private ulong formInspectionContentId;
    private ushort formTooltipParentId;
    private long formTooltipRequestedAt;

    public PopupCaptureRecorder(
        IAddonLifecycle addonLifecycle,
        IGameInventory gameInventory,
        InventoryService inventoryService,
        ChocoboStatsService chocoboStatsService,
        IPluginLog log,
        Func<bool> isAutomationRunning,
        string configDirectory)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameInventory = gameInventory;
        this.inventoryService = inventoryService;
        this.chocoboStatsService = chocoboStatsService;
        this.log = log;
        this.isAutomationRunning = isAutomationRunning;
        this.configDirectory = configDirectory;
    }

    public PopupCaptureKind? ActiveKind { get; private set; }
    public bool IsCapturing => ActiveKind.HasValue;

    public bool InspectFormDetails(out string message)
    {
        if (inspectingForm.HasValue || formInspectionQueue.Count > 0)
        {
            message = "Breeding form inspection is already running.";
            return false;
        }
        if (!GameHelpers.IsPlayerAvailable() || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] ||
            isAutomationRunning())
        {
            message = "Inspect breeding forms while idle and outside a duty.";
            return false;
        }
        var detail = Plugin.GameGui.GetAddonByName("ItemDetail");
        if (!detail.IsNull && detail.IsVisible)
        {
            message = "Close the current item tooltip before inspecting breeding forms.";
            return false;
        }
        foreach (var form in ChocoboInventoryModel.Enumerate(inventoryService)
            .Where(form => form.Kind is ChocoboFormKind.Fledgling or ChocoboFormKind.Retired)
            .OrderBy(form => form.Kind).ThenByDescending(form => form.Pedigree))
            formInspectionQueue.Enqueue(form);
        formInspectionContentId = Plugin.PlayerState.ContentId;
        message = $"Inspecting {formInspectionQueue.Count} breeding forms using their native inventory tooltips.";
        log.Information($"[ChokeAbo][FormDetail] {message}");
        return formInspectionQueue.Count > 0;
    }

    public void UpdateFormInspection()
    {
        if (!inspectingForm.HasValue && formInspectionQueue.Count == 0) return;
        if (!GameHelpers.IsPlayerAvailable() || Plugin.PlayerState.ContentId != formInspectionContentId ||
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] || isAutomationRunning())
        {
            CancelFormInspection();
            return;
        }
        var stage = AtkStage.Instance();
        var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentItemDetail.Instance();
        if (stage == null || agent == null)
        {
            CancelFormInspection();
            return;
        }
        if (inspectingForm is { } pending)
        {
            if (Environment.TickCount64 - formTooltipRequestedAt < 50) return;
            var handle = Plugin.GameGui.GetAddonByName("ItemDetail");
            var identityMatches = agent->DetailKind == FFXIVClientStructs.FFXIV.Client.Enums.DetailKind.InventoryItem &&
                agent->TypeOrId == (uint)pending.Container && agent->Index == pending.Slot && agent->ItemId == pending.ItemId;
            if (identityMatches && !handle.IsNull && handle.IsReady && handle.IsVisible &&
                handle.AtkValuesCount <= 4096 && ((AtkUnitBase*)handle.Address)->UldManager.NodeListCount <= 2048)
            {
                log.Information($"[ChokeAbo][FormDetail] {FormatForm(pending)}; nativeTooltipIdentityVerified=true");
                foreach (var (value, index) in handle.AtkValues.Take(handle.AtkValuesCount).Select((value, index) => (value, index)))
                {
                    if ((int)value.ValueType == (int)FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Undefined) continue;
                    string decoded;
                    try { decoded = FormatValue(value.GetValue()); } catch (NotImplementedException) { decoded = "<unsupported>"; }
                    log.Information($"[ChokeAbo][FormDetail] value[{index}] {value.ValueType}: {RedactLocalName(decoded)}");
                }
                foreach (var line in BuildAddonSnapshot((AtkUnitBase*)handle.Address))
                    log.Information($"[ChokeAbo][FormDetail] {RedactLocalName(line)}");
                var inventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                var currentContainer = inventory == null ? null : inventory->GetInventoryContainer(pending.Container);
                var currentItem = currentContainer == null || pending.Slot >= currentContainer->Size
                    ? null : currentContainer->GetInventorySlot(pending.Slot);
                if (currentItem != null && currentItem->ItemId == pending.ItemId && currentItem->Quantity == pending.Quantity)
                {
                    var abilityId = ((uint)currentItem->Materia[4] << 4) | currentItem->MateriaGrades[4];
                    var colourId = currentItem->Stains[0];
                    var addon = (AtkUnitBase*)handle.Address;
                    var abilityText = GetNodeText(addon->GetNodeById(1000101));
                    var colourText = GetNodeText(addon->GetNodeById(1000102));
                    var abilityName = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ChocoboRaceAbility>().TryGetRow(abilityId, out var ability)
                        ? ability.Name.ExtractText() : string.Empty;
                    var colourName = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Stain>().GetRow(colourId).Name.ExtractText();
                    log.Information($"[ChokeAbo][FormDetail] mappingItem={pending.ItemId}; candidateAbilityEncoding=(Materia[4]<<4)|MateriaGrades[4]; candidateAbilityId={abilityId}; candidateAbilityName={abilityName}; abilityDisplayMatches={!string.IsNullOrWhiteSpace(abilityName) && abilityText.EndsWith($": {abilityName}", StringComparison.Ordinal)}; colourField=Stains[0]; colourId={colourId}; colourName={colourName}; colourDisplayMatches={!string.IsNullOrWhiteSpace(colourName) && colourText.EndsWith($": {colourName}", StringComparison.Ordinal)}; productionMatchingEnabled=false");
                }
                stage->TooltipManager.HideTooltip(formTooltipParentId);
                inspectingForm = null;
            }
            else if (Environment.TickCount64 - formTooltipRequestedAt >= 2000)
            {
                log.Warning($"[ChokeAbo][FormDetail] Native tooltip identity/readiness unverified for {FormKey(pending)}; kind={agent->DetailKind}; typeOrId={agent->TypeOrId}; index={agent->Index}; item={agent->ItemId}; addonReady={!handle.IsNull && handle.IsReady}; addonVisible={!handle.IsNull && handle.IsVisible}; inspection stopped.");
                CancelFormInspection();
            }
            return;
        }
        var parentHandle = Plugin.GameGui.GetAddonByName("_BagWidget");
        if (parentHandle.IsNull || !parentHandle.IsReady || !parentHandle.IsVisible)
        {
            CancelFormInspection();
            return;
        }
        var formToInspect = formInspectionQueue.Dequeue();
        var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(formToInspect.Container);
        if (container == null || formToInspect.Slot < 0 || formToInspect.Slot >= container->Size) return;
        var item = container->GetInventorySlot(formToInspect.Slot);
        if (item == null || item->ItemId != formToInspect.ItemId || item->Quantity != formToInspect.Quantity) return;
        var parent = (AtkUnitBase*)parentHandle.Address;
        if (parent->RootNode == null) return;
        AtkTooltipManager.AtkTooltipArgs args = default;
        args.Ctor();
        args.ItemArgs.InventoryType = formToInspect.Container;
        args.ItemArgs.Slot = checked((short)formToInspect.Slot);
        args.ItemArgs.Kind = FFXIVClientStructs.FFXIV.Client.Enums.DetailKind.InventoryItem;
        formTooltipParentId = parent->Id;
        inspectingForm = formToInspect;
        formTooltipRequestedAt = Environment.TickCount64;
        stage->TooltipManager.ShowTooltip(AtkTooltipType.Item, parent->Id, parent->RootNode, &args);
    }

    public void CancelFormInspection()
    {
        if (inspectingForm is { } pending)
        {
            var stage = AtkStage.Instance();
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentItemDetail.Instance();
            if (stage != null && agent != null && agent->DetailKind == FFXIVClientStructs.FFXIV.Client.Enums.DetailKind.InventoryItem &&
                agent->TypeOrId == (uint)pending.Container && agent->Index == pending.Slot && agent->ItemId == pending.ItemId)
                stage->TooltipManager.HideTooltip(formTooltipParentId);
        }
        formInspectionQueue.Clear();
        inspectingForm = null;
        formInspectionContentId = 0;
    }

    public void Inspect(string? addonName = null)
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
            Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51]) return;
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return;
        var racer = chocoboStatsService.ReadActiveRacerSnapshot();
        var nativePlayer = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (nativePlayer != null)
            log.Information($"[ChokeAbo][Inspect] GoldSaucerContentStatus={nativePlayer->GoldSaucerContentStatus}");
        foreach (var duty in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>()
            .Where(row => row.TerritoryType.RowId is 389 or 390 or 391 or 417))
            log.Information($"[ChokeAbo][Inspect] racingDuty={duty.RowId}; name={duty.Name}; territory={duty.TerritoryType.RowId}");
        var inventoryAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventory.Instance();
        if (inventoryAgent != null)
            log.Information($"[ChokeAbo][Inspect] inventoryAgent active={inventoryAgent->IsAgentActive()}; contextOwner={inventoryAgent->CurrentInventoryContextEvent != null}; openType={inventoryAgent->OpenType}; openTitle={inventoryAgent->OpenTitleId}; addonId={inventoryAgent->AddonId}");
        log.Information($"[ChokeAbo][Inspect] target={Plugin.TargetManager.Target?.BaseId}; occupied={Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInQuestEvent]}; available={GameHelpers.IsPlayerAvailable()}");
        var shopAgentState = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentShop.Instance();
        var shopProxyState = FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler.AgentProxy.Instance();
        if (shopAgentState != null && shopProxyState != null)
            log.Information($"[ChokeAbo][Inspect] shopActive={shopAgentState->IsAgentActive()}; shopAddon={shopAgentState->AddonId}; shopReceiverMatches={(nint)shopAgentState->EventReceiver == (nint)shopProxyState}; shopProxyAddon={shopProxyState->AddonId}; shopHandlerPresent={shopProxyState->Handler != null}");
        if (shopProxyState != null && shopProxyState->Handler != null)
        {
            var handler = shopProxyState->Handler;
            log.Information($"[ChokeAbo][Inspect] retainedShopHandler event={handler->Info.EventId.Id:X}; items={handler->ItemsCount}; startingBuy={handler->StartingBuy}; waiting={handler->WaitingForTransactionToFinish}; item5={handler->Items[5].ItemId}; item6={handler->Items[6].ItemId}; objects={handler->EventObjects.Count}");
        }
        log.Information($"[ChokeAbo][Inspect] conditions={string.Join(",", Enum.GetValues<Dalamud.Game.ClientState.Conditions.ConditionFlag>().Where(flag => (int)flag > 0 && (int)flag < Plugin.Condition.MaxEntries && Plugin.Condition[flag]))}");
        var actions = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (actions != null)
            log.Information($"[ChokeAbo][Inspect] teleportActionStatus={actions->GetActionStatus(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, 5)}; animationLock={actions->AnimationLock}");
        var telepo = FFXIVClientStructs.FFXIV.Client.Game.UI.Telepo.Instance();
        if (telepo != null && telepo->TeleportList.Count is >= 0 and <= 512)
            foreach (var destination in telepo->TeleportList)
                if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRow(destination.TerritoryId).PlaceName.Value.Name.ToString().Contains("Gold Saucer", StringComparison.Ordinal))
                    log.Information($"[ChokeAbo][Inspect] GoldSaucerTeleport={destination.AetheryteId}; cost={destination.GilCost}; territory={destination.TerritoryId}");
        log.Information($"[ChokeAbo][Inspect] racerLoaded={racer.IsLoaded}; pedigree={racer.Pedigree}; racingRank={racer.Rank}; sex={racer.Sex}; territory={Plugin.ClientState.TerritoryType}; observationOnly=true");
        log.Information($"[ChokeAbo][Inspect] availableTrainingSessions={chocoboStatsService.Snapshot.SessionsAvailable}");
        var racerDetails = chocoboStatsService.Snapshot;
        log.Information($"[ChokeAbo][Inspect] racerDataAvailable={racerDetails.IsLoaded}; inheritedAbility={racerDetails.InheritedAbilityId}; learnedAbility={racerDetails.LearnedAbilityId}; colour={racerDetails.ColourId}");
        log.Information($"[ChokeAbo][Inspect] gil={inventoryService.GetItemCount(1)}; MGP={inventoryService.GetItemCount(29)}; level={Plugin.ObjectTable.LocalPlayer?.Level}");
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player != null)
        {
            log.Information($"[ChokeAbo][Inspect] position={player.Position}");
            foreach (var npc in Plugin.ObjectTable.Where(obj => obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc)
                .OrderBy(obj => System.Numerics.Vector3.DistanceSquared(player.Position, obj.Position)).Take(12))
                log.Information($"[ChokeAbo][Inspect] npc={npc.BaseId}; name={npc.Name.TextValue}; position={npc.Position}; distance={System.Numerics.Vector3.Distance(player.Position, npc.Position):F2}; targetable={npc.IsTargetable}");
        }
        foreach (var questId in new ushort[] { 434, 435, 436, 565, 576 })
            log.Information($"[ChokeAbo][Inspect] quest={questId}; completed={FFXIVClientStructs.FFXIV.Client.Game.QuestManager.IsQuestComplete(questId)}");
        var nativeInventory = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        foreach (var form in ChocoboInventoryModel.Enumerate(inventoryService))
        {
            log.Information($"[ChokeAbo][Inspect] {FormatForm(form)}");
            if (nativeInventory == null || form.Kind is not (ChocoboFormKind.Fledgling or ChocoboFormKind.Retired)) continue;
            var container = nativeInventory->GetInventoryContainer(form.Container);
            if (container == null || form.Slot < 0 || form.Slot >= container->Size) continue;
            var slot = container->GetInventorySlot(form.Slot);
            if (slot == null || slot->ItemId != form.ItemId || slot->Quantity <= 0) continue;
            // Record declared native fields only. Their form-specific encoding is not yet a matching rule.
            log.Information($"[ChokeAbo][Inspect] formItem={form.ItemId}; container={form.Container}; slot={form.Slot}; spiritbond={slot->SpiritbondOrCollectability}; glamour={slot->GlamourId}; stains={string.Join(",", slot->Stains.ToArray())}; materia={string.Join(",", slot->Materia.ToArray())}; materiaGrades={string.Join(",", slot->MateriaGrades.ToArray())}; rawFormFields=true");
            var candidateAbilityId = ((uint)slot->Materia[4] << 4) | slot->MateriaGrades[4];
            if (Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ChocoboRaceAbility>().TryGetRow(candidateAbilityId, out var candidateAbility))
                log.Information($"[ChokeAbo][Inspect] abilityItem={form.ItemId}; container={form.Container}; slot={form.Slot}; candidateAbilityEncoding=(Materia[4]<<4)|MateriaGrades[4]; candidateAbilityId={candidateAbilityId}; candidateAbilityName={candidateAbility.Name.ExtractText()}; productionMatchingEnabled=false");
            if (ChocoboInventoryModel.TryReadColour(form, out var colourId))
                log.Information($"[ChokeAbo][Inspect] colourItem={form.ItemId}; container={form.Container}; slot={form.Slot}; colourId={colourId}; colourName={Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Stain>().GetRow(colourId).Name.ExtractText()}; productionMatchingEnabled=false");
        }
        ref var loaded = ref manager->AtkUnitManager.AllLoadedUnitsList;
        var visible = new List<string>();
        for (var i = 0; i < Math.Min((int)loaded.Count, 256); ++i)
        {
            var addon = loaded.Entries[i].Value;
            if (addon != null && addon->IsVisible) visible.Add(addon->NameString);
        }
        log.Information($"[ChokeAbo][Inspect] visible addons: {string.Join(", ", visible)}");
        var names = string.IsNullOrEmpty(addonName) ? visible.Where(IsRelevantAddon).Take(16) : visible.Where(name => name == addonName);
        foreach (var name in names)
        {
            var handle = Plugin.GameGui.GetAddonByName(name);
            if (handle.IsNull || !handle.IsReady || !handle.IsVisible) continue;
            var addon = (AtkUnitBase*)handle.Address;
            if (handle.AtkValuesCount > 4096 || addon->UldManager.NodeListCount > 2048)
            {
                log.Information($"[ChokeAbo][Inspect] addon={name}; values={handle.AtkValuesCount}; nodes={addon->UldManager.NodeListCount}; exceeds bounded capture limits.");
                continue;
            }
            var agent = Plugin.GameGui.FindAgentInterface(handle);
            var agentName = "unknown";
            if (!agent.IsNull)
                foreach (var id in Enum.GetValues<FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId>())
                    if (Plugin.GameGui.GetAgentById((int)id).Address == agent.Address) { agentName = id.ToString(); break; }
            log.Information($"[ChokeAbo][Inspect] addon={name}; id={addon->Id}; parent={addon->ParentId}; host={addon->HostId}; blockedParent={addon->BlockedParentId}; agent={agentName}; values={handle.AtkValuesCount}");
            if (name == "Shop")
            {
                var shopAgent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentShop.Instance();
                var proxy = FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler.AgentProxy.Instance();
                if (shopAgent != null && proxy != null && (nint)shopAgent->EventReceiver == (nint)proxy &&
                    proxy->AddonId == addon->Id && proxy->Handler != null)
                {
                    var shop = proxy->Handler;
                    log.Information($"[ChokeAbo][Inspect] gilShop mapped=True; items={shop->ItemsCount}; visible={shop->VisibleItemsCount}; buyIndex={shop->BuyItemIndex}; selected={shopAgent->SelectedItemIndex}");
                    if (shop->ItemsCount >= 0 && shop->ItemsCount <= shop->Items.Length &&
                        shop->VisibleItemsCount >= 0 && shop->VisibleItemsCount <= shop->VisibleItems.Length)
                        for (var row = 0; row < shop->VisibleItemsCount; ++row)
                        {
                            var index = shop->VisibleItems[row];
                            if (index < 0 || index >= shop->ItemsCount) continue;
                            var item = shop->Items[index];
                            log.Information($"[ChokeAbo][Inspect] gilShop row={row}; itemIndex={index}; item={item.ItemId}; price={item.PriceBuy}; owned={item.NumOwned}; name={item.ItemName}");
                        }
                }
            }
            foreach (var (value, index) in handle.AtkValues.Take(handle.AtkValuesCount).Select((value, index) => (value, index)))
            {
                if ((int)value.ValueType == (int)FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.Undefined) continue;
                string decoded;
                try { decoded = FormatValue(value.GetValue()); } catch (NotImplementedException) { decoded = "<unsupported>"; }
                log.Information($"[ChokeAbo][Inspect] value[{index}] {value.ValueType}: {RedactLocalName(decoded)}");
            }
            foreach (var line in BuildAddonSnapshot(addon)) log.Information($"[ChokeAbo][Inspect] {RedactLocalName(line)}");
            for (var i = 0; i < addon->UldManager.NodeListCount; ++i)
            {
                var node = addon->UldManager.NodeList[i];
                if (node == null || !node->IsVisible()) continue;
                if ((ushort)node->Type >= 1000 && node->GetAsAtkComponentNode()->Component != null)
                {
                    var component = node->GetAsAtkComponentNode()->Component;
                    if (component->GetComponentType() is ComponentType.List or ComponentType.TreeList)
                    {
                        var list = (AtkComponentList*)component;
                        for (var row = 0; row < Math.Min(list->ListLength, 64); ++row)
                        {
                            var renderer = list->GetItemRenderer(row);
                            var label = renderer != null && renderer->ButtonTextNode != null
                                ? renderer->ButtonTextNode->NodeText.ToString() : list->GetItemLabel(row).ToString();
                            log.Information($"[ChokeAbo][Inspect] list={node->NodeId}; row={row}; text={RedactLocalName(label)}");
                            if (renderer != null && row == list->SelectedItemIndex && renderer->UldManager.NodeListCount <= 128)
                            {
                                for (var childIndex = 0; childIndex < renderer->UldManager.NodeListCount; ++childIndex)
                                {
                                    var child = renderer->UldManager.NodeList[childIndex];
                                    if (child == null || !child->IsVisible()) continue;
                                    log.Information($"[ChokeAbo][Inspect] selectedRow={row}; child={child->NodeId}; type={child->Type}; text={GetNodeText(child)}");
                                    var eventCount = 0;
                                    for (var evt = child->AtkEventManager.Event; evt != null && eventCount++ < 16; evt = evt->NextEvent)
                                        log.Information($"[ChokeAbo][Inspect] selectedRow={row}; child={child->NodeId}; event={evt->State.EventType}; param={evt->Param}; addonListener={evt->Listener == (AtkEventListener*)addon}; rendererListener={evt->Listener == (AtkEventListener*)renderer}; global={evt->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent)}");
                                }
                            }
                        }
                    }
                }
                var count = 0;
                for (var evt = node->AtkEventManager.Event; evt != null && count++ < 32; evt = evt->NextEvent)
                    log.Information($"[ChokeAbo][Inspect] node={node->NodeId}; event={evt->State.EventType}; param={evt->Param}; addonListener={evt->Listener == (AtkEventListener*)addon}; global={evt->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent)}");
            }
        }
    }

    private static string RedactLocalName(string text)
    {
        var name = Plugin.ObjectTable.LocalPlayer?.Name.TextValue;
        return string.IsNullOrEmpty(name) ? text : text.Replace(name, "<PLAYER_1>", StringComparison.Ordinal);
    }

    public bool Toggle(PopupCaptureKind kind, out string message)
    {
        if (ActiveKind == kind)
        {
            Stop();
            message = $"Stopped {kind} capture.";
            return true;
        }

        return Start(kind, out message);
    }

    public bool Start(PopupCaptureKind kind, out string message, bool ownedExecution = false)
    {
        if (ActiveKind.HasValue)
        {
            message = $"{ActiveKind.Value} capture is already active; stop it first.";
            return false;
        }
        if (isAutomationRunning() && !ownedExecution)
        {
            message = "Capture refused while Choke-abo automation is running.";
            return false;
        }

        Directory.CreateDirectory(configDirectory);
        var path = Path.Combine(configDirectory, GetFileName(kind));
        writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ActiveKind = kind;
        startingInventory = ChocoboInventoryModel.Enumerate(inventoryService);
        lastAddonSnapshot.Clear();

        Write(string.Empty);
        Write($"## Session {DateTimeOffset.UtcNow:O} — {kind}");
        WriteActiveRacer("start");
        WriteInventory("start", startingInventory);
        foreach (var lifecycleEvent in LifecycleEvents)
            addonLifecycle.RegisterListener(lifecycleEvent, OnAddonLifecycle);
        gameInventory.InventoryChangedRaw += OnInventoryChanged;
        writer.Flush();
        message = $"Started {kind} capture in {GetFileName(kind)}.";
        return true;
    }

    public void Stop()
    {
        if (!ActiveKind.HasValue)
            return;

        foreach (var lifecycleEvent in LifecycleEvents)
            addonLifecycle.UnregisterListener(lifecycleEvent, OnAddonLifecycle);
        gameInventory.InventoryChangedRaw -= OnInventoryChanged;

        var endingInventory = ChocoboInventoryModel.Enumerate(inventoryService);
        WriteActiveRacer("stop");
        WriteInventory("stop", endingInventory);
        WriteInventoryDeltas(startingInventory, endingInventory);
        Write($"## End {DateTimeOffset.UtcNow:O}");
        writer?.Flush();
        writer?.Dispose();
        writer = null;
        ActiveKind = null;
        startingInventory = Array.Empty<ChocoboInventoryForm>();
        lastAddonSnapshot.Clear();
    }

    public void OpenFolder()
    {
        Directory.CreateDirectory(configDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = configDirectory,
            UseShellExecute = true,
        });
    }

    public void Dispose()
    {
        CancelFormInspection();
        Stop();
    }

    private void OnAddonLifecycle(AddonEvent eventType, AddonArgs args)
    {
        if (!IsCapturing || !IsRelevantAddon(args.AddonName))
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (args is AddonReceiveEventArgs hover && IsHoverEvent(hover.AtkEventType.ToString()))
                return;

            var snapshotLines = addon != null && eventType != AddonEvent.PreFinalize
                ? BuildAddonSnapshot(addon)
                : Array.Empty<string>();
            var snapshotSignature = string.Join("\n", snapshotLines);
            var snapshotChanged = !lastAddonSnapshot.TryGetValue(args.AddonName, out var previous) ||
                                  previous != snapshotSignature;
            if (eventType == AddonEvent.PostRefresh && !snapshotChanged)
                return;
            if (addon != null && eventType != AddonEvent.PreFinalize && snapshotChanged)
                lastAddonSnapshot[args.AddonName] = snapshotSignature;

            Write($"- addon `{Escape(args.AddonName)}` lifecycle `{eventType}`");
            if (args is AddonSetupArgs setup)
            {
                var values = setup.AtkValueEnumerable
                    .Select((value, index) => $"{index}:{value.ValueType}={Escape(FormatValue(value.GetValue()))}");
                Write($"  - setup values: {string.Join(" | ", values)}");
            }

            if (args is AddonReceiveEventArgs receive)
                WriteReceiveEvent(addon, args.AddonName, receive);

            if (snapshotChanged)
            {
                foreach (var line in snapshotLines)
                    Write($"  - {line}");
            }
            writer?.Flush();
        }
        catch (Exception ex)
        {
            log.Warning($"[ChokeAbo][Capture] Failed to record {args.AddonName} {eventType}: {ex.Message}");
        }
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (!IsCapturing)
            return;

        foreach (var inventoryEvent in events)
        {
            ref readonly var item = ref inventoryEvent.Item;
            if (item.ItemId < ChocoboInventoryModel.ProofOfCoveringItemId ||
                item.ItemId > ChocoboInventoryModel.LastCoveringPermissionItemId)
            {
                continue;
            }

            var snapshot = new InventoryItemSnapshot(
                item.ItemId,
                ResolveItemName(item.ItemId),
                item.Condition,
                (uint)Math.Max(0, item.Quantity),
                (FFXIVClientStructs.FFXIV.Client.Game.InventoryType)item.ContainerType,
                (int)item.InventorySlot);
            ChocoboInventoryForm.TryDecode(snapshot, out var form);
            Write($"- inventory `{inventoryEvent.Type}`: {FormatForm(form)}");
        }
        writer?.Flush();
    }

    private void WriteReceiveEvent(AtkUnitBase* addon, string addonName, AddonReceiveEventArgs receive)
    {
        var node = FindEventNode(addon, receive.AtkEvent);
        var nodeText = node == null ? string.Empty : GetNodeText(node);
        Write(
            $"  - user event type=`{receive.AtkEventType}` parameter=`{receive.EventParam}` " +
            $"clickedNodeId=`{(node == null ? "unknown" : node->NodeId.ToString(CultureInfo.InvariantCulture))}` " +
            $"clickedText=`{Escape(nodeText)}`");

        var selections = EnumerateListSelections(addon).ToArray();
        foreach (var selection in selections)
        {
            Write(
                $"    - list node={selection.NodeId} selectedIndex={selection.SelectedIndex} " +
                $"rowText=`{Escape(selection.RowText)}`");
        }

        if (addonName is "SelectString" or "SelectIconString")
        {
            var owner = ResolveOwner(addon);
            foreach (var selection in selections)
            {
                Write(
                    $"    - popup owner=`{Escape(owner)}` rowIndex={selection.SelectedIndex} " +
                    $"rowText=`{Escape(selection.RowText)}`");
            }
        }
    }

    private static string[] BuildAddonSnapshot(AtkUnitBase* addon)
    {
        var lines = new List<string>();
        if (addon->UldManager.NodeList != null)
        {
            for (var index = 0; index < addon->UldManager.NodeListCount; index++)
            {
                var node = addon->UldManager.NodeList[index];
                if (node == null || !node->NodeFlags.HasFlag(NodeFlags.Visible))
                    continue;

                var state = $"visible=true enabled={node->NodeFlags.HasFlag(NodeFlags.Enabled).ToString().ToLowerInvariant()}";
                var text = GetNodeText(node);
                lines.Add($"node={node->NodeId} type={node->Type} text=`{Escape(text)}` {state}");
            }
        }

        return lines.ToArray();
    }

    private void WriteActiveRacer(string boundary)
    {
        var racer = chocoboStatsService.ReadActiveRacerSnapshot();
        Write(
            $"- active racer {boundary}: loaded={racer.IsLoaded.ToString().ToLowerInvariant()} " +
            $"rank={racer.Rank} pedigree={racer.Pedigree} sex={racer.Sex}");
    }

    private void WriteInventory(string boundary, IReadOnlyList<ChocoboInventoryForm> forms)
    {
        Write($"- inventory {boundary}:");
        if (forms.Count == 0)
        {
            Write("  - none in item range 9560-9614");
            return;
        }

        foreach (var form in forms)
            Write($"  - {FormatForm(form)}");
    }

    private void WriteInventoryDeltas(
        IReadOnlyList<ChocoboInventoryForm> before,
        IReadOnlyList<ChocoboInventoryForm> after)
    {
        Write("- inventory start-to-stop deltas:");
        var beforeMap = before.ToDictionary(FormKey);
        var afterMap = after.ToDictionary(FormKey);
        var keys = beforeMap.Keys.Union(afterMap.Keys).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (keys.Length == 0)
        {
            Write("  - none");
            return;
        }

        foreach (var key in keys)
        {
            beforeMap.TryGetValue(key, out var start);
            afterMap.TryGetValue(key, out var stop);
            Write(
                $"  - {key}: quantity {start.Quantity}->{stop.Quantity} " +
                $"delta={(long)stop.Quantity - start.Quantity}; condition/capacity {start.Capacity}->{stop.Capacity} " +
                $"delta={(long)stop.Capacity - start.Capacity}");
        }
    }

    private static AtkResNode* FindEventNode(AtkUnitBase* addon, nint atkEventPointer)
    {
        if (addon == null || atkEventPointer == 0 || addon->UldManager.NodeList == null)
            return null;

        var atkEvent = (AtkEvent*)atkEventPointer;
        for (var index = 0; index < addon->UldManager.NodeListCount; index++)
        {
            var node = addon->UldManager.NodeList[index];
            if (node == null)
                continue;
            if (node == atkEvent->Node || (AtkEventTarget*)node == atkEvent->Target)
                return node;
        }

        return null;
    }

    private static IReadOnlyList<(uint NodeId, int SelectedIndex, string RowText)> EnumerateListSelections(AtkUnitBase* addon)
    {
        var result = new List<(uint NodeId, int SelectedIndex, string RowText)>();
        if (addon == null || addon->UldManager.NodeList == null)
            return result;

        for (var index = 0; index < addon->UldManager.NodeListCount; index++)
        {
            var node = addon->UldManager.NodeList[index];
            if (node == null || (ushort)node->Type < 1000)
                continue;

            var componentNode = node->GetAsAtkComponentNode();
            var component = componentNode == null ? null : componentNode->Component;
            if (component == null || component->GetComponentType() != ComponentType.List)
                continue;

            var list = (AtkComponentList*)component;
            var selected = list->SelectedItemIndex;
            var rowText = selected >= 0 && selected < list->ListLength
                ? list->GetItemLabel(selected).ToString()
                : string.Empty;
            result.Add((node->NodeId, selected, rowText));
        }

        return result;
    }

    internal static string GetNodeText(AtkResNode* node)
    {
        if (node == null)
            return string.Empty;
        if (node->Type == NodeType.Text)
            return node->GetAsAtkTextNode()->NodeText.ToString();
        if ((ushort)node->Type < 1000)
            return string.Empty;

        var componentNode = node->GetAsAtkComponentNode();
        var component = componentNode == null ? null : componentNode->Component;
        if (component == null || component->UldManager.NodeList == null || component->UldManager.NodeListCount > 512)
            return string.Empty;

        var texts = new List<string>();
        for (var index = 0; index < component->UldManager.NodeListCount; index++)
        {
            var child = component->UldManager.NodeList[index];
            if (child == null || child->Type != NodeType.Text || !child->NodeFlags.HasFlag(NodeFlags.Visible))
                continue;
            var text = child->GetAsAtkTextNode()->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
                texts.Add(text);
        }
        return string.Join(" / ", texts.Distinct(StringComparer.Ordinal));
    }

    private static string ResolveOwner(AtkUnitBase* addon)
    {
        if (addon == null || addon->HostId == 0)
            return "none";
        var owner = RaptureAtkUnitManager.Instance()->GetAddonById(addon->HostId);
        return owner == null ? $"hostId:{addon->HostId}" : owner->NameString;
    }

    private string ResolveItemName(uint itemId)
        => inventoryService.ResolveItemName(itemId);

    private static bool IsRelevantAddon(string name)
        => name.Contains("Chocobo", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("Race", StringComparison.OrdinalIgnoreCase) ||
           name is "SelectString" or "SelectIconString" or "SelectYesno" or "ShopExchangeCurrency" or "ContextMenu" or "ItemDetail" or "Talk" or "GoldSaucerInfo" or "Shop" or "ContentsFinder" or "ContentsFinderConfirm" or "HowTo";

    private static bool IsHoverEvent(string eventName)
        => eventName.Contains("MouseOver", StringComparison.OrdinalIgnoreCase) ||
           eventName.Contains("MouseOut", StringComparison.OrdinalIgnoreCase) ||
           eventName.Contains("MouseMove", StringComparison.OrdinalIgnoreCase);

    private static string GetFileName(PopupCaptureKind kind)
        => kind switch
        {
            PopupCaptureKind.Retirement => "retirement.md",
            PopupCaptureKind.CoveringSelector => "cselector.md",
            PopupCaptureKind.FledglingSelector => "fselector.md",
            _ => "adoption.md",
        };

    private static string FormKey(ChocoboInventoryForm form)
        => $"item={form.ItemId} {form.Container}#{form.Slot}";

    private static string FormatForm(ChocoboInventoryForm form)
        => $"item={form.ItemId} name=`{Escape(form.ItemName)}` kind={form.Kind} pedigree=G{form.Pedigree} " +
           $"sex={form.Sex} condition/capacity={form.Capacity} container={form.Container} slot={form.Slot} quantity={form.Quantity}";

    private static string FormatValue(object? value)
        => value?.ToString() ?? "null";

    private static string Escape(string? value)
        => (value ?? string.Empty).Replace("`", "\\`").Replace("\r", "\\r").Replace("\n", "\\n");

    private void Write(string line)
        => writer?.WriteLine($"{DateTimeOffset.UtcNow:O} {line}");
}
