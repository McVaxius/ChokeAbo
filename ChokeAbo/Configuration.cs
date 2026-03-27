using Dalamud.Configuration;
using System;

namespace ChokeAbo;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
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
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
