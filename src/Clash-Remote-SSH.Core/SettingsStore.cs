using System.Text.Json;

namespace ClashRemoteSsh.Core;

public sealed class SettingsStore
{
    public string DirectoryPath { get; }
    public SettingsStore(string? directory = null) => DirectoryPath = directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashOnWebSSH");

    public ConnectionProfile Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ConnectionProfile>(File.ReadAllText(Path.Combine(DirectoryPath, "settings.json"))) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(ConnectionProfile profile)
    {
        profile.Validate();
        Directory.CreateDirectory(DirectoryPath);
        var target = Path.Combine(DirectoryPath, "settings.json");
        File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(target + ".tmp", target, true);
    }
}

public sealed class HostKeyStore
{
    private readonly string _path;
    public HostKeyStore(string directory) => _path = Path.Combine(directory, "known-hosts.json");
    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_path)) return new();
        // A damaged trust store must never silently reset trust.
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ??
            throw new InvalidOperationException("主机指纹文件损坏，请核对后修复。");
    }

    public bool Verify(HostIdentity identity, Func<HostIdentity, bool> confirm)
    {
        var keys = Read();
        var key = $"{identity.Host.ToLowerInvariant()}:{identity.Port}";
        if (keys.TryGetValue(key, out var expected))
        {
            if (expected != identity.Fingerprint)
                throw new InvalidOperationException("服务器 SSH 指纹发生变化，已停止连接。请先核实服务器身份。");
            return true;
        }
        if (!confirm(identity)) return false;
        keys[key] = identity.Fingerprint;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(_path + ".tmp", _path, true);
        return true;
    }
}
