using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace ChokeAbo.Services;

public readonly record struct TargetCycleEnsureRequest(
    int Version,
    ulong ContentId,
    int TargetPedigree,
    int RetirementRank,
    int PreferredFeedGrade);

public readonly record struct TargetCycleIdentityRequest(int Version, ulong ContentId);

public sealed record TargetCycleStatus(
    int Version,
    ulong ContentId,
    string Phase,
    bool ShouldBlockRacing,
    bool TargetReady,
    bool GameActionInProgress,
    string Reason,
    DateTimeOffset? NextCoveringEligibilityUtc);

public static class TargetCycleProtocol
{
    public const int Version = 2;

    public static string ToPhaseName(BreedingPhase phase)
        => phase switch
        {
            BreedingPhase.Idle => "Idle",
            BreedingPhase.Planning => "Planning",
            BreedingPhase.PurchasingFeed => "PurchasingFeed",
            BreedingPhase.Feeding => "Feeding",
            BreedingPhase.Racing => "Racing",
            BreedingPhase.RetirementPendingCapture => "RetirementPendingCapture",
            BreedingPhase.CoveringPendingCapture => "CoveringPendingCapture",
            BreedingPhase.CoveringWait => "CoveringWait",
            BreedingPhase.AdoptionPendingCapture => "AdoptionPendingCapture",
            BreedingPhase.RegistrationPendingCapture => "RegistrationPendingCapture",
            BreedingPhase.Paused => "Paused",
            BreedingPhase.TargetReady => "TargetReady",
            _ => "Blocked",
        };

    public static bool TryParseEnsure(string json, out TargetCycleEnsureRequest request, out string error)
    {
        request = default;
        if (!TryParseRoot(json, out var document, out error))
            return false;

        using (document)
        {
            var root = document.RootElement;
            if (!TryReadInt32(root, "version", out var version, out error) ||
                !TryReadUInt64(root, "contentId", out var contentId, out error) ||
                !TryReadInt32(root, "targetPedigree", out var targetPedigree, out error) ||
                !TryReadInt32(root, "retirementRank", out var retirementRank, out error) ||
                !TryReadInt32(root, "preferredFeedGrade", out var preferredFeedGrade, out error))
            {
                return false;
            }

            if (version != Version)
            {
                error = $"Unsupported version {version}; expected {Version}.";
                return false;
            }
            if (contentId == 0)
            {
                error = "contentId must be an unsigned non-zero value.";
                return false;
            }
            if (targetPedigree is < 2 or > 9)
            {
                error = "targetPedigree must be in G2-G9.";
                return false;
            }
            if (retirementRank is < 40 or > 50)
            {
                error = "retirementRank must be in 40-50.";
                return false;
            }
            if (preferredFeedGrade is < 1 or > 3)
            {
                error = "preferredFeedGrade must be in 1-3.";
                return false;
            }

            request = new TargetCycleEnsureRequest(version, contentId, targetPedigree, retirementRank, preferredFeedGrade);
            return true;
        }
    }

    public static bool TryParseIdentity(string json, out TargetCycleIdentityRequest request, out string error)
    {
        request = default;
        if (!TryParseRoot(json, out var document, out error))
            return false;

        using (document)
        {
            var root = document.RootElement;
            if (!TryReadInt32(root, "version", out var version, out error) ||
                !TryReadUInt64(root, "contentId", out var contentId, out error))
            {
                return false;
            }

            if (version != Version)
            {
                error = $"Unsupported version {version}; expected {Version}.";
                return false;
            }
            if (contentId == 0)
            {
                error = "contentId must be an unsigned non-zero value.";
                return false;
            }

            request = new TargetCycleIdentityRequest(version, contentId);
            return true;
        }
    }

    private static bool TryParseRoot(string json, out JsonDocument document, out string error)
    {
        document = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "JSON request is empty.";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return true;

            document.Dispose();
            error = "JSON request root must be an object.";
            return false;
        }
        catch (JsonException ex)
        {
            error = $"Malformed JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryReadInt32(JsonElement root, string name, out int value, out string error)
    {
        value = 0;
        if (root.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out value))
        {
            error = string.Empty;
            return true;
        }

        error = $"Required integer field '{name}' is missing or invalid.";
        return false;
    }

    private static bool TryReadUInt64(JsonElement root, string name, out ulong value, out string error)
    {
        value = 0;
        if (root.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetUInt64(out value))
        {
            error = string.Empty;
            return true;
        }

        error = $"Required unsigned field '{name}' is missing or invalid.";
        return false;
    }
}

public sealed class BreedingIpcProvider : IDisposable
{
    public const string ShouldBlockRacingChannel = "ChokeAbo.Breeding.ShouldBlockRacing.V1";
    public const string EnsureTargetCycleChannel = "ChokeAbo.Breeding.EnsureTargetCycle.V2";
    public const string GetTargetCycleStatusChannel = "ChokeAbo.Breeding.GetTargetCycleStatus.V2";
    public const string PauseTargetCycleChannel = "ChokeAbo.Breeding.PauseTargetCycle.V2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BreedingService service;
    private readonly ICallGateProvider<bool> shouldBlockRacingProvider;
    private readonly ICallGateProvider<string, string> ensureTargetCycleProvider;
    private readonly ICallGateProvider<string, string> getTargetCycleStatusProvider;
    private readonly ICallGateProvider<string, string> pauseTargetCycleProvider;

    public BreedingIpcProvider(IDalamudPluginInterface pluginInterface, BreedingService service)
    {
        this.service = service;
        shouldBlockRacingProvider = pluginInterface.GetIpcProvider<bool>(ShouldBlockRacingChannel);
        ensureTargetCycleProvider = pluginInterface.GetIpcProvider<string, string>(EnsureTargetCycleChannel);
        getTargetCycleStatusProvider = pluginInterface.GetIpcProvider<string, string>(GetTargetCycleStatusChannel);
        pauseTargetCycleProvider = pluginInterface.GetIpcProvider<string, string>(PauseTargetCycleChannel);
        shouldBlockRacingProvider.RegisterFunc(service.ShouldBlockRacing);
        ensureTargetCycleProvider.RegisterFunc(EnsureTargetCycle);
        getTargetCycleStatusProvider.RegisterFunc(GetTargetCycleStatus);
        pauseTargetCycleProvider.RegisterFunc(PauseTargetCycle);
    }

    public void Dispose()
    {
        pauseTargetCycleProvider.UnregisterFunc();
        getTargetCycleStatusProvider.UnregisterFunc();
        ensureTargetCycleProvider.UnregisterFunc();
        shouldBlockRacingProvider.UnregisterFunc();
    }

    private string EnsureTargetCycle(string json)
        => TargetCycleProtocol.TryParseEnsure(json, out var request, out var error)
            ? Serialize(service.EnsureTargetCycle(request))
            : Serialize(InvalidStatus(error));

    private string GetTargetCycleStatus(string json)
        => TargetCycleProtocol.TryParseIdentity(json, out var request, out var error)
            ? Serialize(service.GetTargetCycleStatus(request.ContentId))
            : Serialize(InvalidStatus(error));

    private string PauseTargetCycle(string json)
        => TargetCycleProtocol.TryParseIdentity(json, out var request, out var error)
            ? Serialize(service.PauseTargetCycle(request.ContentId))
            : Serialize(InvalidStatus(error));

    private static string Serialize(TargetCycleStatus status)
        => JsonSerializer.Serialize(status, JsonOptions);

    private static TargetCycleStatus InvalidStatus(string reason)
        => new(
            TargetCycleProtocol.Version,
            Plugin.PlayerState.ContentId,
            "Blocked",
            true,
            false,
            false,
            reason,
            null);
}
