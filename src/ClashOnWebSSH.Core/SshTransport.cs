using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;

namespace ClashOnWebSSH.Core;

public interface ISshTransport : IDisposable
{
    Task<CommandResult> ExecuteAsync(string command, string input, TimeSpan timeout, CancellationToken cancellation = default);
    Task<CommandResult> UploadAsync(string command, ReadOnlyMemory<byte> header, Stream input, long length,
        IProgress<long>? progress, TimeSpan timeout, CancellationToken cancellation = default)
        => throw new NotSupportedException("当前 SSH 通道不支持二进制上传。");
}

public sealed partial class OpenSshTransport : ISshTransport
{
    private readonly ConnectionProfile _profile;
    public OpenSshTransport(ConnectionProfile profile) { profile.Validate(); _profile = profile; }
    public static ProcessStartInfo BuildStartInfo(ConnectionProfile profile, string command)
    {
        profile.Validate();
        var executable = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe") : "/usr/bin/ssh";
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到系统 OpenSSH，请安装 Windows OpenSSH 客户端，或改用账号密码登录。", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=12", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2", "-p", profile.Port.ToString() })
            start.ArgumentList.Add(arg);
        if (!string.IsNullOrWhiteSpace(profile.IdentityFile))
        { start.ArgumentList.Add("-i"); start.ArgumentList.Add(profile.IdentityFile); }
        start.ArgumentList.Add($"{profile.Username}@{profile.Host}");
        start.ArgumentList.Add(command);
        return start;
    }

    public async Task<CommandResult> ExecuteAsync(string command, string input, TimeSpan timeout, CancellationToken cancellation = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await ExecuteOnceAsync(command, input, timeout, cancellation);
            // This error occurs before SSH authentication/command dispatch. Retrying it
            // cannot repeat a remote write. Never retry an interrupted running command.
            if (attempt >= 2 || result.ExitCode != 255 || !result.Error.Contains("kex_exchange_identification", StringComparison.Ordinal)) return result;
            await Task.Delay(TimeSpan.FromSeconds(2 + attempt), cancellation);
        }
    }

    private async Task<CommandResult> ExecuteOnceAsync(string command, string input, TimeSpan timeout, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = BuildStartInfo(_profile, command) };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(timeout);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(budget.Token);
        var stderr = process.StandardError.ReadToEndAsync(budget.Token);
        try
        {
            try { await process.StandardInput.WriteAsync(input.AsMemory(), budget.Token); }
            catch (IOException) { /* SSH may exit during handshake; collect its real stderr below. */ }
            try { process.StandardInput.Close(); } catch (IOException) { }
            await process.WaitForExitAsync(budget.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException("SSH 请求中断或超时。远端操作可能已经完成，请刷新状态后再决定是否重试。");
        }
    }
    public void Dispose() { }
}

public sealed partial class PasswordSshTransport : ISshTransport
{
    private readonly ConnectionProfile _profile;
    private readonly SshClient _client;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private string? _hostError;
    public PasswordSshTransport(ConnectionProfile profile, string password, Func<HostIdentity, bool> verifyHost)
    {
        profile.Validate();
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("请输入 SSH 密码。");
        _profile = profile;
        var passwordMethod = new PasswordAuthenticationMethod(profile.Username, password);
        var interactive = new KeyboardInteractiveAuthenticationMethod(profile.Username);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase) || prompt.Request.Contains("密码")) prompt.Response = password;
            }
        };
        _client = new SshClient(new ConnectionInfo(profile.Host, profile.Port, profile.Username, passwordMethod, interactive)
        { Timeout = TimeSpan.FromSeconds(15), Encoding = Encoding.UTF8 });
        _client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
            try { e.CanTrust = verifyHost(new(_profile.Host, _profile.Port, e.HostKeyName, fingerprint)); }
            catch (Exception error) { _hostError = error.Message; e.CanTrust = false; }
        };
        _client.KeepAliveInterval = TimeSpan.FromSeconds(15);
    }

    public Task<CommandResult> ExecuteAsync(string command, string input, TimeSpan timeout, CancellationToken cancellation = default)
        // SSH.NET opens command channels synchronously before returning its Task.
        // Keep both channel negotiation and authentication callbacks off the UI thread.
        => Task.Run(() => ExecuteCoreAsync(command, input, timeout, cancellation), cancellation);

    private async Task<CommandResult> ExecuteCoreAsync(string command, string input, TimeSpan timeout, CancellationToken cancellation)
    {
        await _serial.WaitAsync(cancellation);
        try
        {
            if (!_client.IsConnected)
            {
                _hostError = null;
                try { await _client.ConnectAsync(cancellation); }
                catch (Exception error) when (_hostError != null) { throw new InvalidOperationException(_hostError, error); }
            }
            using var remote = _client.CreateCommand(command, Encoding.UTF8);
            remote.CommandTimeout = timeout;
            var execute = remote.ExecuteAsync(cancellation);
            if (input.Length > 0)
            {
                using var stream = remote.CreateInputStream();
                var bytes = Encoding.UTF8.GetBytes(input);
                await stream.WriteAsync(bytes, cancellation);
                await stream.FlushAsync(cancellation);
            }
            await execute;
            return new(remote.ExitStatus ?? -1, remote.Result, remote.Error);
        }
        finally { _serial.Release(); }
    }
    public void Dispose() { _client.Dispose(); _serial.Dispose(); }
}
