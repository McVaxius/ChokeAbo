namespace ChokeAbo;

internal static class PluginInfo
{
    public const string DisplayName = "Choke-abo";
    public const string InternalName = "ChokeAbo";
    public const string Command = "/chokeabo";
    public const string Visibility = "Public";
    public const string Summary = "Race chocobo stat planning with vendor and trainer automation seams.";
    public const string SupportUrl = "https://ko-fi.com/mcvaxius";
    public const string DiscordUrl = "https://discord.gg/VsXqydsvpu";
    public const string DiscordFeedbackNote = "Scroll down to \"The Dumpster Fire\" channel to discuss issues / suggestions for specific plugins.";
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
        "Refresh racing chocobo stats from the main window",
        "Edit the training plan and confirm projected stats change"
    };
}
