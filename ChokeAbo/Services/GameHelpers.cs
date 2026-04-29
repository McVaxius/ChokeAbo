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

    public static bool TryTravelToChocoboSquare()
    {
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
