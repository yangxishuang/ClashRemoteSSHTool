using System.Text;
using System.Text.Json;

namespace ClashOnWebSSH.Core;

public sealed partial class RemoteActions
{
    private readonly SemaphoreSlim _deployments = new(1, 1);
    private static string CoreUploadCommand => BuildScriptCommand(CoreScript + "\nreceive_core_main()\n") + " # CLASHSSH_CORE_UPLOAD";
    private static string GeodataUploadCommand => BuildScriptCommand(CoreScript + "\nreceive_geodata_main()\n") + " # CLASHSSH_GEODATA_UPLOAD";

    public async Task<RemoteReply> DeployAsync(string profileId, IProgress<DeploymentProgress>? progress = null,
        string? offlinePackage = null, KernelPackageStore? packages = null, CancellationToken cancellation = default, GeodataPackageStore? geodata = null)
    {
        await _deployments.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            progress?.Report(new("检查远端架构与已有内核"));
            var plan = (await InvokeAsync("core.status", new { profileId }, cancellation).ConfigureAwait(false)).Data;
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
                await UploadCoreAsync(path, release, progress, cancellation).ConfigureAwait(false);
            }
            var preparedGeodata = plan.TryGetProperty("geodata", out var resources) && resources.GetArrayLength() > 0;
            if (preparedGeodata)
            {
                var entries = resources.EnumerateArray().ToArray();
                if (entries.Length != GeodataRelease.All.Count || entries.Select(a => a.GetProperty("name").GetString()).Distinct().Count() != entries.Length)
                    throw new InvalidOperationException("远端规则数据清单不完整。");
                foreach (var entry in entries)
                {
                    var asset = GeodataRelease.ForName(entry.GetProperty("name").GetString()!);
                    if (entry.GetProperty("version").GetString() != asset.Version || entry.GetProperty("sha256").GetString() != asset.Sha256 || entry.GetProperty("size").GetInt64() != asset.Size)
                        throw new InvalidOperationException("远端规则数据清单与本机不一致。");
                    if (entry.GetProperty("cached").GetBoolean()) continue;
                    geodata ??= new GeodataPackageStore();
                    var path = await geodata.AcquireAsync(asset.Name, progress, cancellation).ConfigureAwait(false);
                    await UploadGeodataAsync(path, asset, progress, cancellation).ConfigureAwait(false);
                }
            }
            progress?.Report(new("远端校验配置并启动服务（规则数据已准备）"));
            return await InvokeAsync("deploy", new { profileId, uploadedCore = true, preparedGeodata }, cancellation).ConfigureAwait(false);
        }
        finally { _deployments.Release(); }
    }

    private async Task UploadGeodataAsync(string path, GeodataRelease asset, IProgress<DeploymentProgress>? progress, CancellationToken cancellation)
    {
        await using var input = File.OpenRead(path);
        var header = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { version = asset.Version, asset = asset.Name, size = input.Length, sha256 = asset.Sha256 }) + "\n");
        var stage = "SSH 上传规则数据 · " + asset.Name;
        progress?.Report(new(stage, 0, input.Length));
        await _operations.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var result = await _transport.UploadAsync(GeodataUploadCommand, header, input, input.Length,
                new InlineProgress<long>(sent => progress?.Report(new(stage, sent, asset.Size))), TimeSpan.FromMinutes(5), cancellation).ConfigureAwait(false);
            if (result.ExitCode != 0 && !result.Output.Contains("@@CLASHSSH@@")) throw new InvalidOperationException("规则数据 SSH 上传失败。");
            var reply = RemoteReply.Parse(result.Output); reply.EnsureSuccess();
            if (reply.Data.GetProperty("size").GetInt64() != asset.Size || reply.Data.GetProperty("sha256").GetString() != asset.Sha256)
                throw new InvalidOperationException("远端未确认规则数据完整性。");
        }
        finally { _operations.Release(); }
    }
}

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
