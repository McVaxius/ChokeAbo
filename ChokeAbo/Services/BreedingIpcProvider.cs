using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace ChokeAbo.Services;

public enum BreedingMode { OwnedParents, NpcPermits }
public enum InsufficientFeedPolicy { FallBack, Skip, Stop }

public readonly record struct TargetCycleEnsureRequest(
    int Version,
    ulong ContentId,
    int TargetPedigree,
    int RetirementRank,
    int PreferredFeedGrade,
    BreedingMode BreedingMode = BreedingMode.OwnedParents,
    uint GilReserve = 0,
    uint MgpReserve = 0,
    InsufficientFeedPolicy FeedPolicy = InsufficientFeedPolicy.FallBack,
    bool Resume = false,
    bool RaceAdmissionAllowed = true,
    bool ProduceCounterpart = false,
    OffspringGoal OffspringGoal = OffspringGoal.ReachPedigree,
    uint DesiredInheritedAbilityId = 0,
    uint[]? AcceptableColourIds = null,
    int RequestedOffspring = 0,
    bool StartNewBatch = false);

public readonly record struct TargetCycleIdentityRequest(int Version, ulong ContentId);

public sealed record TargetCycleStatus(
    int Version,
    ulong ContentId,
    string Phase,
    bool ShouldBlockRacing,
    bool TargetReady,
    bool GameActionInProgress,
    string Reason,
    DateTimeOffset? NextCoveringEligibilityUtc,
    int Pedigree = 0,
    int RacingRank = 0,
    bool ProgressionComplete = false,
    bool CanResumeOwnedInteraction = false,
    uint InheritedAbilityId = 0,
    uint LearnedAbilityId = 0,
    uint ColourId = 0,
    bool RacerDataAvailable = false,
    OffspringGoal OffspringGoal = OffspringGoal.ReachPedigree,
    int MatchingOffspringProduced = 0,
    int MatchingOffspringRequested = 0,
    long OffspringCollected = 0,
    bool ProductionComplete = false);

public static class TargetCycleProtocol
{
    public const int Version = 2;

    public static string ToPhaseName(BreedingPhase phase)
        => phase switch
        {
            BreedingPhase.Idle => "Idle",
            BreedingPhase.Planning => "Planning",
            BreedingPhase.PurchasingFeed => "PurchasingFeed",
            BreedingPhase.PurchasingSupplies => "PurchasingSupplies",
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

    public static bool TryParseEnsure(string json, out TargetCycleEnsureRequest request, out string error, int expectedVersion = Version)
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

            if (version != expectedVersion)
            {
                error = $"Unsupported version {version}; expected {expectedVersion}.";
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
            if (expectedVersion == 3)
            {
                if (!TryReadInt32(root, "breedingMode", out var mode, out error) ||
                    !TryReadInt32(root, "feedPolicy", out var policy, out error) ||
                    !TryReadUInt64(root, "gilReserve", out var gil, out error) ||
                    !TryReadUInt64(root, "mgpReserve", out var mgp, out error)) return false;
                if (!Enum.IsDefined((BreedingMode)mode) || !Enum.IsDefined((InsufficientFeedPolicy)policy) ||
                    gil > uint.MaxValue || mgp > uint.MaxValue)
                {
                    error = "Invalid breeding mode, feed policy, or currency reserve.";
                    return false;
                }
                request = request with { BreedingMode = (BreedingMode)mode, FeedPolicy = (InsufficientFeedPolicy)policy,
                    GilReserve = (uint)gil, MgpReserve = (uint)mgp };
                if (!root.TryGetProperty("raceAdmissionAllowed", out var admission) ||
                    admission.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { error = "V3 requires the current race admission allowance."; return false; }
                request = request with { RaceAdmissionAllowed = admission.GetBoolean() };
                if (root.TryGetProperty("produceCounterpart", out var counterpart))
                {
                    if (counterpart.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                        counterpart.GetBoolean() && request.BreedingMode != BreedingMode.NpcPermits)
                    { error = "Counterpart covering requires a boolean choice and NPC permit mode."; return false; }
                    request = request with { ProduceCounterpart = counterpart.GetBoolean() };
                }
            }
            return true;
        }
    }

    public static bool TryParseIdentity(string json, out TargetCycleIdentityRequest request, out string error, int expectedVersion = Version)
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

            if (version != expectedVersion)
            {
                error = $"Unsupported version {version}; expected {expectedVersion}.";
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

    public static bool TryParseWorkflow(string json, out TargetCycleEnsureRequest request, out string error)
    {
        if (!TryParseEnsure(json, out request, out error, 3)) return false;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryReadInt32(root, "offspringGoal", out var goal, out error) || !Enum.IsDefined((OffspringGoal)goal))
        { error = "A valid offspringGoal is required by the workflow operation."; return false; }
        request = request with { OffspringGoal = (OffspringGoal)goal };
        if (request.OffspringGoal == OffspringGoal.ReachPedigree) return true;
        if (request.TargetPedigree != 9 || request.ProduceCounterpart ||
            !TryReadInt32(root, "requestedOffspring", out var quantity, out error) || quantity < 1)
        { error = "Production requires G9, a positive requested quantity and no progression-counterpart request."; return false; }
        request = request with { RequestedOffspring = quantity };
        if (request.OffspringGoal == OffspringGoal.AbilityOffspring)
        {
            if (!TryReadUInt64(root, "desiredInheritedAbilityId", out var abilityId, out error) || abilityId is 0 or > byte.MaxValue)
            { error = "Choose a valid desired inherited ability."; return false; }
            request = request with { DesiredInheritedAbilityId = (uint)abilityId };
        }
        else
        {
            if (!root.TryGetProperty("acceptableColourIds", out var colours) || colours.ValueKind != JsonValueKind.Array)
            { error = "Choose the acceptable offspring colours."; return false; }
            var ids = new List<uint>();
            foreach (var colour in colours.EnumerateArray())
            {
                if (colour.ValueKind != JsonValueKind.Number || !colour.TryGetUInt32(out var id) || id is 0 or > byte.MaxValue || ids.Contains(id))
                { error = "Acceptable colours must be distinct valid colour IDs."; return false; }
                ids.Add(id);
            }
            if (ids.Count == 0) { error = "Choose at least one acceptable offspring colour."; return false; }
            request = request with { AcceptableColourIds = ids.Order().ToArray() };
        }
        return true;
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
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (document.RootElement.EnumerateObject().All(property => names.Add(property.Name))) return true;
                document.Dispose();
                error = "Duplicate JSON fields are not permitted.";
                return false;
            }

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
    private readonly List<ICallGateProvider<string, string>> v3Providers = new();

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
        RegisterV3(pluginInterface, "EnsureTargetCycle", json => EnsureV3(json, false));
        RegisterV3(pluginInterface, "ResumeTargetCycle", json => EnsureV3(json, true));
        // Separate operations prevent older V3 builds from silently advancing the pedigree.
        RegisterV3(pluginInterface, "EnsureCounterpartCycle", json => EnsureV3(json, false, true));
        RegisterV3(pluginInterface, "ResumeCounterpartCycle", json => EnsureV3(json, true, true));
        RegisterV3(pluginInterface, "EnsureWorkflow", json => EnsureWorkflow(json, false));
        RegisterV3(pluginInterface, "ResumeWorkflow", json => EnsureWorkflow(json, true));
        RegisterV3(pluginInterface, "StartWorkflow", json => EnsureWorkflow(json, true, true));
        RegisterV3(pluginInterface, "GetTargetCycleStatus", json => IdentityV3(json, false));
        RegisterV3(pluginInterface, "PauseTargetCycle", json => IdentityV3(json, true));
        RegisterV3(pluginInterface, "SuspendTargetCycle", json => Serialize(
            (TargetCycleProtocol.TryParseIdentity(json, out var request, out var error, 3)
                ? service.SuspendTargetCycle(request.ContentId) : InvalidStatus(error)) with { Version = 3 }));
    }

    private void RegisterV3(IDalamudPluginInterface pluginInterface, string operation, Func<string, string> handler)
    {
        var provider = pluginInterface.GetIpcProvider<string, string>($"ChokeAbo.Breeding.{operation}.V3");
        provider.RegisterFunc(handler);
        v3Providers.Add(provider);
    }

    private string EnsureV3(string json, bool resume, bool counterpart = false)
        => Serialize((service.CurrentOffspringGoal != OffspringGoal.ReachPedigree
            ? InvalidStatus("The saved offspring workflow requires the current V3 workflow endpoints; older callers cannot replace it.")
            : TargetCycleProtocol.TryParseEnsure(json, out var request, out var error, 3)
            ? counterpart && (request.BreedingMode != BreedingMode.NpcPermits || !request.ProduceCounterpart)
                ? InvalidStatus("Counterpart operation requires an explicit NPC permit counterpart request.")
                : service.EnsureTargetCycle(request with { Resume = resume })
            : InvalidStatus(error)) with { Version = 3 });

    private string EnsureWorkflow(string json, bool resume, bool startNewBatch = false)
        => Serialize((TargetCycleProtocol.TryParseWorkflow(json, out var request, out var error)
            ? service.EnsureTargetCycle(request with { Resume = resume, StartNewBatch = startNewBatch }) : InvalidStatus(error)) with { Version = 3 });

    private string IdentityV3(string json, bool pause)
        => Serialize((TargetCycleProtocol.TryParseIdentity(json, out var request, out var error, 3)
            ? (pause ? service.PauseTargetCycle(request.ContentId) : service.GetTargetCycleStatus(request.ContentId))
            : InvalidStatus(error)) with { Version = 3 });

    public void Dispose()
    {
        foreach (var provider in v3Providers) provider.UnregisterFunc();
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
