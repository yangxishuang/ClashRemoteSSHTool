using System.Reflection;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace ClashOnWebSSH.Core;

public sealed partial class RemoteActions : IDisposable
{
    private readonly ISshTransport _transport;
    private readonly ConnectionProfile _profile;
    private readonly SemaphoreSlim _operations = new(1, 1);
    // JSON goes to encrypted stdin, never into HTML or a shell expression.
    private static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException("缺少远端适配资源。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
    private static string CoreScript => "CORE_MANIFEST = __import__('json').loads(" + JsonSerializer.Serialize(Resource("kernel-manifest.json")) + ")\n" + Resource("core_transfer.py")
        + "\nGEODATA_MANIFEST = __import__('json').loads(" + JsonSerializer.Serialize(Resource("geodata-manifest.json")) + ")\n" + Resource("geodata.py");
    public static string Script { get; } = CoreScript + "\n" + Resource("profiles.py") + "\n" + Resource("remote.py");
    public static string ScriptCommand { get; } = BuildScriptCommand(Script);
    private static string NetworkScript { get; } = "__name__='clashssh_network'\n" + Script +
        "\nNETWORK_ROLLBACK_SCRIPT = " + JsonSerializer.Serialize(Resource("tun_rollback.py")) + "\n" + Resource("network.py") + "\nmain()\n";
    // Code travels on stdin so the Windows command-line length stays bounded.
    internal const string NetworkCommand = "python3 -u -c \"import sys,base64,zlib;exec(zlib.decompress(base64.b64decode(sys.stdin.readline())))\" # CLASHSSH_NETWORK";
    private static string EncodeScript(string script)
    {
        using var buffer = new MemoryStream();
        using (var compressor = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) compressor.Write(Encoding.UTF8.GetBytes(script));
        return Convert.ToBase64String(buffer.ToArray());
    }
    private static string BuildScriptCommand(string script)
    {
        using var buffer = new MemoryStream();
        using (var compressor = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(Encoding.UTF8.GetBytes(script));
        return "python3 -u -c \"import base64,zlib;exec(zlib.decompress(base64.b64decode('" + Convert.ToBase64String(buffer.ToArray()) + "')))\"";
    }
    public RemoteActions(ConnectionProfile profile, ISshTransport transport) { _profile = profile; _transport = transport; }

    public async Task<string> CheckConnectionAsync(CancellationToken cancellation = default)
    {
        var command = "printf 'CLASHSSH_READY\\n'; uname -sm; command -v python3 || true; command -v systemctl || true; " +
            "printf 'CLASHSSH_SERVICE='; systemctl --user show " + ShellQuote(_profile.ServiceName) + " -p LoadState --value 2>/dev/null || true";
        var result = await _transport.ExecuteAsync(command, "", TimeSpan.FromSeconds(25), cancellation);
        if (result.ExitCode != 0 || !result.Output.Contains("CLASHSSH_READY"))
            throw new InvalidOperationException(Redactor.Clean(result.Error.Length > 0 ? result.Error : "SSH 连接未确认"));
        return result.Output;
    }

    public async Task<RemoteReply> InvokeAsync(string action, object? values = null, CancellationToken cancellation = default)
    {
        var allowed = new[] { "probe", "core.status", "groups", "select", "delay", "service", "logs", "connectivity", "mode", "update", "deploy",
            "profiles.list", "profiles.source", "profiles.preview", "profiles.import", "profiles.apply", "profiles.update", "profiles.rename", "profiles.delete", "uninstall.plan", "uninstall",
            "network.status", "network.test", "network.mode", "network.system-proxy", "network.tun.prepare", "network.tun.set", "network.tun.confirm" };
        if (!allowed.Contains(action)) throw new ArgumentException("不支持的远端动作。");
        await _operations.WaitAsync(cancellation);
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["action"] = action, ["service"] = _profile.ServiceName,
                ["config"] = _profile.ConfigPath, ["apiPort"] = _profile.ControllerPort
            };
            if (values != null)
                foreach (var entry in JsonSerializer.SerializeToElement(values, WireJson).EnumerateObject())
                {
                    if (payload.ContainsKey(entry.Name)) throw new ArgumentException("不能覆盖固定操作参数。");
                    payload[entry.Name] = entry.Value;
                }
            var seconds = action is "deploy" or "update" or "profiles.import" or "profiles.preview" or "profiles.apply" or "profiles.update" ? 600
                : action == "delay" ? 2400 : action == "network.tun.prepare" ? 360 : action is "service" or "uninstall" ? 120 : action is "network.tun.set" or "network.system-proxy" or "network.mode" ? 240 : action.StartsWith("network.") ? 45 : 25;
            var network = action.StartsWith("network.");
            var input = (network ? EncodeScript(NetworkScript) + "\n" : "") + JsonSerializer.Serialize(payload, WireJson);
            var result = await _transport.ExecuteAsync(network ? NetworkCommand : ScriptCommand, input, TimeSpan.FromSeconds(seconds), cancellation);
            if (result.ExitCode != 0 && !result.Output.Contains("@@CLASHSSH@@"))
                throw new InvalidOperationException(Redactor.Clean(result.Error.Length > 0 ? result.Error : result.Output));
            var reply = RemoteReply.Parse(result.Output);
            reply.EnsureSuccess();
            return reply;
        }
        finally { _operations.Release(); }
    }

    public async Task<RemotePrivilegeMode> CheckPrivilegesAsync(CancellationToken cancellation = default)
    {
        await _operations.WaitAsync(cancellation);
        try
        {
            // A harmless shell command. -k with a command ignores cached authentication;
            // a recent password-authenticated sudo must not be mistaken for NOPASSWD.
            const string command = "if [ \"$(id -u)\" = 0 ]; then printf 'CLASHSSH_PRIVILEGE=root\\n'; " +
                "elif ! command -v sudo >/dev/null 2>&1; then printf 'CLASHSSH_PRIVILEGE=unavailable\\n'; " +
                "elif sudo -n -k -- sh -c ':' </dev/null >/dev/null 2>&1; then printf 'CLASHSSH_PRIVILEGE=passwordless\\n'; " +
                "else printf 'CLASHSSH_PRIVILEGE=password\\n'; fi";
            var result = await _transport.ExecuteAsync(command, "", TimeSpan.FromSeconds(25), cancellation);
            if (result.ExitCode != 0) return RemotePrivilegeMode.Unknown;
            var marker = result.Output.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.StartsWith("CLASHSSH_PRIVILEGE=", StringComparison.Ordinal));
            return marker switch
            {
                "CLASHSSH_PRIVILEGE=root" => RemotePrivilegeMode.Root,
                "CLASHSSH_PRIVILEGE=passwordless" => RemotePrivilegeMode.PasswordlessSudo,
                "CLASHSSH_PRIVILEGE=password" => RemotePrivilegeMode.PasswordRequired,
                "CLASHSSH_PRIVILEGE=unavailable" => RemotePrivilegeMode.SudoUnavailable,
                _ => RemotePrivilegeMode.Unknown
            };
        }
        finally { _operations.Release(); }
    }

    public async Task<string> BootstrapAsync(string sudoPassword, bool enableLinger, CancellationToken cancellation = default)
    {
        await _operations.WaitAsync(cancellation);
        try
        {
            var script = Resource("bootstrap.sh").Replace("\r\n", "\n");
            // Only the fixed script is in argv. Password is sent over encrypted stdin.
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            var runScript = "printf '%s' '" + encoded + "' | base64 -d | sh -s -- " + (enableLinger ? "linger" : "dependencies");
            var privileged = "sh -c " + ShellQuote(runScript);
            var command = "if [ \"$(id -u)\" = 0 ]; then " + privileged +
                "; else sudo -S -p '' -- " + privileged + "; fi";
            var result = await _transport.ExecuteAsync(command, sudoPassword + "\n", TimeSpan.FromMinutes(8), cancellation);
            if (result.ExitCode != 0) throw new InvalidOperationException(Redactor.Clean(result.Error + "\n" + result.Output));
            return Redactor.Clean(result.Output);
        }
        finally { _operations.Release(); }
    }
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    public void Dispose() { _transport.Dispose(); _operations.Dispose(); _deployments.Dispose(); }
}
