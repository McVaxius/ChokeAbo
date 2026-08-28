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

    public bool Start(PopupCaptureKind kind, out string message)
    {
        if (ActiveKind.HasValue)
        {
            message = $"{ActiveKind.Value} capture is already active; stop it first.";
            return false;
        }
        if (isAutomationRunning())
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
        => Stop();

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
            if (node == null || node->Type < NodeType.Component)
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

    private static string GetNodeText(AtkResNode* node)
    {
        if (node == null)
            return string.Empty;
        if (node->Type == NodeType.Text)
            return node->GetAsAtkTextNode()->NodeText.ToString();
        if (node->Type < NodeType.Component)
            return string.Empty;

        var componentNode = node->GetAsAtkComponentNode();
        var component = componentNode == null ? null : componentNode->Component;
        if (component == null || component->UldManager.NodeList == null)
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
           name is "SelectString" or "SelectIconString" or "SelectYesno" or "ShopExchangeCurrency" or "ContextMenu";

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
