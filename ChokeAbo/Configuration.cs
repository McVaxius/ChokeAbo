using Dalamud.Configuration;
using System;

namespace ChokeAbo;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool PluginEnabled { get; set; } = false;
    public bool DtrBarEnabled { get; set; } = true;
    public int DtrBarMode { get; set; } = 1;
    public string DtrIconEnabled { get; set; } = "\uE044";
    public string DtrIconDisabled { get; set; } = "\uE04C";
    public string LastAccountId { get; set; } = string.Empty;
    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
