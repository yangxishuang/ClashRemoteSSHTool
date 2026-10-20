using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClashOnWebSSH.Core;

public enum AuthenticationMode { SystemSsh, Password }

// Credentials deliberately do not belong in this serializable model.
public sealed record ConnectionProfile
{
    public string Host { get; init; } = "example.invalid";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = "user";
    public AuthenticationMode Authentication { get; init; }
    public string IdentityFile { get; init; } = "";
    public string ServiceName { get; init; } = "clash-verge.service";
    public string ConfigPath { get; init; } = "~/.config/clash-verge/config.yaml";
    public int ControllerPort { get; init; } = 9090;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.StartsWith('-') ||
            Host.Length > 253 || !Regex.IsMatch(Host, @"^[a-zA-Z0-9.:%_-]+$"))
            throw new ArgumentException("请输入有效的服务器 IP 或主机名。");
        if (Port is < 1 or > 65535 || ControllerPort is < 1 or > 65535)
            throw new ArgumentException("端口必须在 1–65535 之间。");
        if (!Regex.IsMatch(Username, @"^[a-zA-Z_][a-zA-Z0-9_.-]{0,63}$"))
            throw new ArgumentException("请输入有效的 SSH 用户名。");
        if (!Regex.IsMatch(ServiceName, @"^[a-zA-Z0-9_.@-]+\.service$") || ServiceName.Length > 120)
            throw new ArgumentException("服务名必须是有效的 .service 用户服务。");
        if (string.IsNullOrWhiteSpace(ConfigPath) || ConfigPath.Any(char.IsControl) || ConfigPath.Length > 1024)
            throw new ArgumentException("配置路径无效。");
        if (Authentication == AuthenticationMode.SystemSsh && IdentityFile.Length > 0 && !File.Exists(IdentityFile))
            throw new ArgumentException("所选私钥文件不存在。");
    }
}

public sealed record CommandResult(int ExitCode, string Output, string Error);
public sealed record HostIdentity(string Host, int Port, string Algorithm, string Fingerprint);
public sealed record RemoteReply(bool Ok, string Message, JsonElement Data)
{
    public static RemoteReply Parse(string output)
    {
        const string marker = "@@CLASHSSH@@";
        var index = output.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("远端未返回有效结果，请确认 Python 3 可用，或先执行「准备依赖」。");
        using var document = JsonDocument.Parse(output[(index + marker.Length)..].Trim());
        var root = document.RootElement;
        return new(root.GetProperty("ok").GetBoolean(),
            root.GetProperty("message").GetString() ?? "",
            root.TryGetProperty("data", out var data) ? data.Clone() : default);
    }

    public void EnsureSuccess()
    {
        if (!Ok) throw new InvalidOperationException(Message);
    }
}

public static class Redactor
{
    public static string Clean(string? value)
    {
        var text = value ?? "";
        text = Regex.Replace(text, @"(?i)https?://[^\s\""'<>]+", match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? uri.Scheme + "://" + uri.Host + "/[已隐藏]" : "[链接已隐藏]");
        text = Regex.Replace(text, @"(?i)((?:password|passwd|token|secret|authorization)\s*[:=]\s*)[^\s,;]+", "$1[已隐藏]");
        return text.Length <= 50000 ? text : text[..50000] + "\n…输出已截断";
    }
}
