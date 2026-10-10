using Dalamud.Configuration;
using ChokeAbo.Services;
using System;
using System.Collections.Generic;

namespace ChokeAbo;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public string UiLanguage { get; set; } = "en";
    public uint UiAccentRgb { get; set; } = Ui.ChokePresentation.ReferenceAccent;
    public bool UiCompact { get; set; } = true;
    public bool UiCompactVisibleOnMainWindow { get; set; }
    public bool UiTransparencyVisibleOnMainWindow { get; set; }
    public bool UiCompactDefaultsApplied { get; set; }
    [Newtonsoft.Json.JsonExtensionData]
    public System.Collections.Generic.Dictionary<string, Newtonsoft.Json.Linq.JToken>? AdditionalSettings { get; set; }

    internal bool ApplyCompactDefaults()
    {
        if (UiCompactDefaultsApplied) return false;
        UiCompact = true;
        UiCompactVisibleOnMainWindow = UiTransparencyVisibleOnMainWindow = false;
        UiCompactDefaultsApplied = true;
        return true;
    }
    public bool UiLanguageVisibleOnMainWindow { get; set; } = true;
    public bool UiTransparencyEnabled { get; set; } = true;
    private int uiWindowOpacityPercent = 100;
    public int UiWindowOpacityPercent { get => uiWindowOpacityPercent; set => uiWindowOpacityPercent = System.Math.Clamp(value, 10, 100); }
    public bool UiAutoFade { get; set; } = true;
    private int uiFadedOpacityPercent = 50;
    public int UiFadedOpacityPercent { get => uiFadedOpacityPercent; set => uiFadedOpacityPercent = System.Math.Clamp(value, 10, 100); }
    private int uiUnfocusedDelaySeconds = 10;
    public int UiUnfocusedDelaySeconds { get => uiUnfocusedDelaySeconds; set => uiUnfocusedDelaySeconds = System.Math.Clamp(value, 0, 3600); }
    public bool PluginEnabled { get; set; } = false;
    public bool DtrBarEnabled { get; set; } = false;
    public int DtrBarMode { get; set; } = 1;
    public string DtrIconEnabled { get; set; } = "\uE044";
    public string DtrIconDisabled { get; set; } = "\uE04C";
    public string LastAccountId { get; set; } = string.Empty;
    public int PlannedMaximumSpeedTrainings { get; set; }
    public int MaximumSpeedFeedGrade { get; set; } = 1;
    public int PlannedAccelerationTrainings { get; set; }
    public int AccelerationFeedGrade { get; set; } = 1;
    public int PlannedEnduranceTrainings { get; set; }
    public int EnduranceFeedGrade { get; set; } = 1;
    public int PlannedStaminaTrainings { get; set; }
    public int StaminaFeedGrade { get; set; } = 1;
    public int PlannedCunningTrainings { get; set; }
    public int CunningFeedGrade { get; set; } = 1;
    public Dictionary<ulong, BreedingCharacterState> BreedingStates { get; set; } = new();
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
