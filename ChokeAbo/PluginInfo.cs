namespace ChokeAbo;

internal static class PluginInfo
{
    public const string DisplayName = "Choke-abo";
    public const string InternalName = "ChokeAbo";
    public const string Command = "/chokeabo";
    public const string Visibility = "Public";
    public const string Summary = "Chocobo stable feeding and vendor-planning scaffold.";
    public const string SupportUrl = "https://ko-fi.com/mcvaxius";
    public static readonly string[] Concept = new[]
    {
        "Track current stats and feed plans.",
        "Reserve stable and vendor automation seams.",
        "Bias toward transparent deterministic flows."
    };
    public static readonly string[] Services = new[]
    {
        "CharacterStateService",
        "RecommendationEngine",
        "InventoryService",
        "VendorPurchaseService",
        "StableFeedingService",
        "PlanStorageService"
    };
    public static readonly string[] Phases = new[]
    {
        "Shell and docs",
        "Inventory truth",
        "Planning math",
        "Vendor flow",
        "Stable loop"
    };
    public static readonly string[] Tests = new[]
    {
        "Load plugin and open UI",
        "Check DTR toggle",
        "Check icon and manifest output"
    };
}
