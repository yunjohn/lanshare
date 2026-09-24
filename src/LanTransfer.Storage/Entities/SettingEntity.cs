namespace LanTransfer.Storage.Entities;

/// <summary>Settings 表的键值实体（用于存放不适合放在 settings.json 的运行时状态）。</summary>
public sealed class SettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
