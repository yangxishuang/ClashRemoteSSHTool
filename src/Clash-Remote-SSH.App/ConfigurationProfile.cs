using System.Windows;
using System.Windows.Media;

namespace ClashRemoteSsh.App;

public sealed class ConfigurationProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string SourceHost { get; set; } = "";
    public string SourceName { get; set; } = "";
    public bool Active { get; set; }
    public double UpdatedAt { get; set; }
    public int NodeCount { get; set; }
    public int ProviderCount { get; set; }
    public Dictionary<string, long> Usage { get; set; } = new();
    public bool IsStored => Id != "current";
    public bool CanApply => IsStored && !Active;
    public bool CanRefresh => Kind == "url" && IsStored;
    public bool CanDelete => IsStored && !Active;
    public string SourceText => Kind == "url" ? "订阅链接 · " + SourceHost : Kind == "existing" ? "服务器现有运行配置" : "YAML 文件 · " + SourceName;
    public string DetailText => Kind == "existing" ? "首次导入时自动保存原有配置" : NodeCount + " 个节点 · " + ProviderCount + " 个提供器";
    public string ApplyText => Active ? "正在使用" : "使用此配置";
    public string UpdateText => Active ? "更新并应用" : "更新订阅";
    public string UsageText => Usage.TryGetValue("total", out var total) && total > 0
        ? $"{FormatBytes(UsedBytes)} / {FormatBytes(total)} · 剩余 {FormatBytes(Math.Max(0, total - UsedBytes))}" : "来源未提供流量信息";
    private double UsedBytes => Math.Max(0d, (double)Usage.GetValueOrDefault("upload") + Usage.GetValueOrDefault("download"));
    public double UsedPercent => Usage.GetValueOrDefault("total") > 0 ? Math.Clamp(UsedBytes / Usage["total"] * 100, 0, 100) : 0;
    public string ExpiryText => !Usage.TryGetValue("expire", out var expiry) ? "未提供到期时间" : expiry == 0 ? "长期有效" : FormatDate(expiry, "yyyy-MM-dd 到期");
    public string UpdatedText => "更新于 " + FormatDate(UpdatedAt, "MM-dd HH:mm");
    public Visibility ActiveVisibility => Active ? Visibility.Visible : Visibility.Collapsed;
    public Brush CardBorder => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Active ? "#8279ED" : "#E2E7F0"));
    public Brush CardBackground => new SolidColorBrush((Color)ColorConverter.ConvertFromString(Active ? "#F5F3FF" : "#FFFFFF"));
    private static string FormatDate(double seconds, string format)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds((long)seconds).ToLocalTime().ToString(format); }
        catch (ArgumentOutOfRangeException) { return "未知"; }
    }
    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var index = 0;
        while (bytes >= 1024 && index < units.Length - 1) { bytes /= 1024; index++; }
        return bytes.ToString("0.#") + " " + units[index];
    }
}
