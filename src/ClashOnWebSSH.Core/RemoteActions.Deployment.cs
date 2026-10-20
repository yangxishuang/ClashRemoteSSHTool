using System.Text;
using System.Text.Json;

namespace ClashOnWebSSH.Core;

public sealed partial class RemoteActions
{
    private readonly SemaphoreSlim _deployments = new(1, 1);
    private static string CoreUploadCommand => BuildScriptCommand(CoreScript + "\nreceive_core_main()\n") + " # CLASHSSH_CORE_UPLOAD";

    public async Task<RemoteReply> DeployAsync(string profileId, IProgress<DeploymentProgress>? progress = null,
        string? offlinePackage = null, KernelPackageStore? packages = null, CancellationToken cancellation = default)
    {
        await _deployments.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            progress?.Report(new("检查远端架构与已有内核"));
            var plan = (await InvokeAsync("core.status", cancellation: cancellation).ConfigureAwait(false)).Data;
            if (plan.GetProperty("installed").GetBoolean()) throw new InvalidOperationException("检测到已有配置或服务，请使用服务管理功能。");
            if (!plan.GetProperty("systemdUser").GetBoolean()) throw new InvalidOperationException("systemd 用户会话不可用，请先准备运行环境。");
            var release = KernelRelease.ForArchitecture(plan.GetProperty("architecture").GetString()!);
            if (plan.GetProperty("version").GetString() != release.Version || plan.GetProperty("asset").GetString() != release.Asset
                || plan.GetProperty("sha256").GetString() != release.Sha256) throw new InvalidOperationException("远端内核清单与本机不一致。");
            if (!plan.GetProperty("coreAvailable").GetBoolean() && !plan.GetProperty("cached").GetBoolean())
            {
                packages ??= new KernelPackageStore();
                var path = offlinePackage == null ? await packages.AcquireAsync(release, progress, cancellation).ConfigureAwait(false)
                    : await packages.ImportAsync(offlinePackage, release, progress, cancellation).ConfigureAwait(false);
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
            progress?.Report(new("远端校验配置并启动服务"));
            return await InvokeAsync("deploy", new { profileId, uploadedCore = true }, cancellation).ConfigureAwait(false);
        }
        finally { _deployments.Release(); }
    }
}

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
