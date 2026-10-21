using System.Text;
using System.Text.Json;

namespace ClashOnWebSSH.Core;

public sealed partial class RemoteActions
{
    public async Task<RemoteReply> SetTunAsync(bool enabled, CancellationToken cancellation = default)
    {
        var result = await InvokeAsync("network.tun.set", new { enabled }, cancellation).ConfigureAwait(false);
        var transaction = result.Data.TryGetProperty("transaction", out var value) ? value.GetString() : null;
        // A separate SSH connection proves the client can still reach the server.
        // Cancellation or failure leaves the server's independent rollback timer armed.
        if (enabled && !string.IsNullOrEmpty(transaction))
        {
            try { result = await InvokeAsync("network.tun.confirm", new { transaction }, cancellation).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new InvalidOperationException("TUN 连通确认未完成，自动回退仍保持；请等待约 75 秒后刷新检查。" + Redactor.Clean(error.Message), error); }
        }
        return result;
    }

    public async Task<RemoteReply> PrepareTunPermissionsAsync(string sudoPassword, IProgress<DeploymentProgress>? progress = null,
        string? offlinePackage = null, KernelPackageStore? packages = null, CancellationToken cancellation = default)
    {
        await _deployments.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var plan = (await InvokeAsync("network.status", cancellation: cancellation).ConfigureAwait(false)).Data;
            if (plan.GetProperty("tun").GetProperty("pending").GetBoolean())
                throw new InvalidOperationException("TUN 正等待连通确认或回退，请稍后刷新。");
            if (!plan.GetProperty("tun").GetProperty("device").GetBoolean())
                throw new InvalidOperationException("服务器没有可用的 /dev/net/tun，请联系服务器或容器管理员。");
            if (!plan.GetProperty("trustedSystemCore").GetBoolean())
            {
                var release = KernelRelease.ForArchitecture(plan.GetProperty("architecture").GetString()!);
                packages ??= new KernelPackageStore();
                var path = offlinePackage == null ? await packages.AcquireAsync(release, progress, cancellation).ConfigureAwait(false)
                    : await packages.ImportAsync(offlinePackage, release, progress, cancellation).ConfigureAwait(false);
                await UploadCoreAsync(path, release, progress, cancellation).ConfigureAwait(false);
            }
            // Disable an old, unusable TUN configuration before granting network privileges.
            if (plan.GetProperty("tun").GetProperty("configured").GetBoolean())
                await SetTunAsync(false, cancellation).ConfigureAwait(false);
            progress?.Report(new("准备独立内核的 TUN 权限"));
            await _operations.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                var script = "CORE_MANIFEST = __import__('json').loads(" + JsonSerializer.Serialize(Resource("kernel-manifest.json")) + ")\n" + Resource("tun_bootstrap.py");
                var privileged = BuildScriptCommand(script);
                var command = "if [ \"$(id -u)\" = 0 ]; then " + privileged + "; else sudo -S -p '' -- " + privileged + "; fi";
                var output = await _transport.ExecuteAsync(command, sudoPassword + "\n", TimeSpan.FromMinutes(8), cancellation).ConfigureAwait(false);
                if (!output.Output.Contains("@@CLASHSSH@@")) throw new InvalidOperationException(Redactor.Clean(output.Error + "\n" + output.Output));
                RemoteReply.Parse(output.Output).EnsureSuccess();
                if (output.ExitCode != 0) throw new InvalidOperationException("TUN 权限准备未成功，请刷新检查。");
            }
            finally { _operations.Release(); }
            progress?.Report(new("应用用户服务权限并重启"));
            return await InvokeAsync("network.tun.prepare", cancellation: cancellation).ConfigureAwait(false);
        }
        finally { _deployments.Release(); }
    }

    private async Task UploadCoreAsync(string path, KernelRelease release, IProgress<DeploymentProgress>? progress, CancellationToken cancellation)
    {
        await using var input = File.OpenRead(path);
        var size = input.Length;
        var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = release.Version, asset = release.Asset, size, sha256 = release.Sha256 }) + "\n");
        progress?.Report(new("SSH 上传内核", 0, size));
        await _operations.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var result = await _transport.UploadAsync(CoreUploadCommand, header, input, size,
                new InlineProgress<long>(sent => progress?.Report(new("SSH 上传内核", sent, size))), TimeSpan.FromMinutes(5), cancellation).ConfigureAwait(false);
            if (result.ExitCode != 0 && !result.Output.Contains("@@CLASHSSH@@"))
                throw new InvalidOperationException(Redactor.Clean(result.Error.Length > 0 ? result.Error : "内核 SSH 上传失败。"));
            var reply = RemoteReply.Parse(result.Output); reply.EnsureSuccess();
            if (reply.Data.GetProperty("size").GetInt64() != size || reply.Data.GetProperty("sha256").GetString() != release.Sha256)
                throw new InvalidOperationException("远端未确认内核完整性。");
        }
        finally { _operations.Release(); }
    }
}
