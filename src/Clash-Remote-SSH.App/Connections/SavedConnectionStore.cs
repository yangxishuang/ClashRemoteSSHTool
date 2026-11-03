using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashRemoteSsh.Core;

namespace ClashRemoteSsh.App.Connections;

// Only this Windows-specific store contains credentials. ConnectionProfile remains credential-free.
public sealed class SavedConnection
{
    public ConnectionProfile Profile { get; init; } = new();
    public string ProtectedPassword { get; init; } = "";
    [JsonIgnore] public string DisplayName => $"{Profile.Username}@{Profile.Host}:{Profile.Port}";
    [JsonIgnore] public string AuthenticationLabel => Profile.Authentication == AuthenticationMode.Password ? "密码登录" : "系统 SSH · 免密登录";
    public override string ToString() => DisplayName;
}

public sealed class SavedConnectionStore(string directory)
{
    private sealed class Document
    {
        public int Version { get; init; } = 1;
        public List<SavedConnection> Connections { get; init; } = [];
    }

    private string FilePath => Path.Combine(directory, "connections.json");
    public static bool SameTarget(ConnectionProfile left, ConnectionProfile right) =>
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port && left.Username == right.Username && left.Authentication == right.Authentication;

    private static byte[] Entropy(ConnectionProfile profile) => SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { app = "ClashOnWebSSH.connections.v1", host = profile.Host.ToLowerInvariant(), profile.Port, profile.Username, profile.Authentication })));

    public IReadOnlyList<SavedConnection> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            if (new FileInfo(FilePath).Length > 1_048_576) throw new JsonException();
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath));
            if (document is null || document.Version != 1 || document.Connections is null || document.Connections.Count > 50)
                throw new JsonException();
            foreach (var entry in document.Connections)
            {
                if (entry?.Profile is null || entry.ProtectedPassword is null || entry.ProtectedPassword.Length > 32768 ||
                    !Enum.IsDefined(entry.Profile.Authentication)) throw new JsonException();
                // Missing/moved private keys must not hide otherwise valid saved entries.
                (entry.Profile with { IdentityFile = "" }).Validate();
            }
            return document.Connections;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new IOException("已保存的连接记录损坏，原文件已保留，请检查 connections.json。", error); }
    }

    public string ReadPassword(SavedConnection connection)
    {
        if (connection.Profile.Authentication != AuthenticationMode.Password || connection.ProtectedPassword.Length == 0) return "";
        byte[]? clear = null;
        try
        {
            clear = ProtectedData.Unprotect(Convert.FromBase64String(connection.ProtectedPassword), Entropy(connection.Profile), DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        { throw new InvalidOperationException("无法解密已保存的 SSH 密码，请重新输入。密码仅可由原 Windows 用户在原电脑上使用。", error); }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    public IReadOnlyList<SavedConnection> Remember(ConnectionProfile profile, string password)
    {
        profile.Validate();
        if (!Enum.IsDefined(profile.Authentication)) throw new ArgumentException("登录方式无效。");
        var entries = Load().Where(entry => !SameTarget(entry.Profile, profile)).ToList();
        var encrypted = "";
        if (profile.Authentication == AuthenticationMode.Password)
        {
            if (Encoding.UTF8.GetByteCount(password) > 16384) throw new IOException("SSH 密码过长，未保存连接记录。");
            var clear = Encoding.UTF8.GetBytes(password);
            try { encrypted = Convert.ToBase64String(ProtectedData.Protect(clear, Entropy(profile), DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        entries.Insert(0, new SavedConnection { Profile = profile, ProtectedPassword = encrypted });
        entries = entries.Take(50).ToList();
        Write(entries); return entries;
    }

    public IReadOnlyList<SavedConnection> Forget(ConnectionProfile profile)
    {
        var entries = Load().Where(entry => !SameTarget(entry.Profile, profile)).ToList();
        Write(entries); return entries;
    }

    private void Write(List<SavedConnection> entries)
    {
        var json = JsonSerializer.Serialize(new Document { Connections = entries }, new JsonSerializerOptions { WriteIndented = true });
        if (Encoding.UTF8.GetByteCount(json) > 1_048_576) throw new IOException("连接记录过大，未保存；请删除不再使用的记录。");
        Directory.CreateDirectory(directory);
        // A unique file avoids collisions between simultaneously open windows; failures preserve the old file.
        var temporary = Path.Combine(directory, ".connections-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
