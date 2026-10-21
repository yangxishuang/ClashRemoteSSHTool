using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClashOnWebSSH.Core;

public sealed record KernelRelease(string Version, string Asset, string Sha256, int ElfMachine)
{
    public string FileName => $"mihomo-linux-{Asset}-{Version}.gz";
    public string OfficialUrl => $"https://github.com/MetaCubeX/mihomo/releases/download/{Version}/{FileName}";
    public static KernelRelease ForArchitecture(string architecture)
    {
        using var stream = typeof(KernelRelease).Assembly.GetManifestResourceStream("kernel-manifest.json")!;
        using var manifest = JsonDocument.Parse(stream);
        if (!manifest.RootElement.GetProperty("assets").TryGetProperty(architecture, out var asset))
            throw new InvalidOperationException("自动部署仅支持 Linux amd64/arm64。");
        return new(manifest.RootElement.GetProperty("version").GetString()!, asset.GetProperty("asset").GetString()!,
            asset.GetProperty("sha256").GetString()!, asset.GetProperty("elfMachine").GetInt32());
    }
}

public sealed record DeploymentProgress(string Stage, long Bytes = 0, long? Total = null)
{
    public double Percent => Total is > 0 ? Math.Min(100, Bytes * 100d / Total.Value) : 0;
    public string Text => Total is > 0 ? $"{Stage} · {Bytes / 1048576d:0.0} / {Total.Value / 1048576d:0.0} MiB · {Percent:0}%"
        : Bytes > 0 ? $"{Stage} · {Bytes / 1048576d:0.0} MiB" : Stage;
}

public sealed class KernelAcquisitionException(string message) : InvalidOperationException(message);

/// <summary>Only public, pinned release assets go through the mirrors. Subscription URLs never do.</summary>
public sealed class KernelPackageStore
{
    public const long MaxPackageBytes = 64 * 1024 * 1024;
    private readonly string _directory;
    private readonly HttpMessageHandler? _testHandler;
    private readonly bool _bundledFallback;
    public KernelPackageStore(string? directory = null, HttpMessageHandler? handler = null, bool bundledFallback = true)
    {
        _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashOnWebSSH", "kernels");
        _testHandler = handler;
        _bundledFallback = bundledFallback;
    }
    private string CachePath(KernelRelease release) => Path.Combine(_directory, release.FileName);

    public async Task<string> AcquireAsync(KernelRelease release, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(_directory);
        var cached = CachePath(release);
        if (File.Exists(cached))
        {
            progress?.Report(new("校验本机内核缓存"));
            try { await ValidateAsync(cached, release, cancellation).ConfigureAwait(false); return cached; }
            catch (InvalidDataException) { /* A damaged cache must never be uploaded. */ }
        }
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        overall.CancelAfter(TimeSpan.FromMinutes(3));
        var sources = new[] { ("GH-Proxy 加速源", "https://gh-proxy.com/" + release.OfficialUrl),
            ("GHFast 加速源", "https://ghfast.top/" + release.OfficialUrl), ("GitHub 官方源", release.OfficialUrl) };
        var failures = new List<string>();
        // Use the user's Windows proxy first; also try a direct route automatically.
        foreach (var useProxy in _testHandler == null ? new[] { true, false } : new[] { true })
        {
            using var client = new HttpClient(_testHandler ?? new HttpClientHandler
            { UseProxy = useProxy, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }, disposeHandler: _testHandler == null)
            { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ClashOnWebSSH/0.3.1");
            foreach (var (name, url) in sources)
            {
                var label = name + (useProxy ? "（本机网络）" : "（直连）");
                var staging = cached + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    progress?.Report(new("本机下载 · " + label));
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(45));
                    await DownloadAsync(client, new Uri(url), staging, label, progress, attempt.Token).ConfigureAwait(false);
                    progress?.Report(new("校验下载内核 · SHA-256 / 架构"));
                    await ValidateAsync(staging, release, attempt.Token).ConfigureAwait(false);
                    File.Move(staging, cached, overwrite: true);
                    return cached;
                }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or InvalidDataException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    failures.Add(label + "：" + (error is InvalidDataException ? error.Message : error is OperationCanceledException ? "连接或下载超时" : "无法完成下载"));
                }
                finally { if (File.Exists(staging)) File.Delete(staging); }
                if (overall.IsCancellationRequested) break;
            }
            if (overall.IsCancellationRequested) break;
        }
        cancellation.ThrowIfCancellationRequested();
        var pinned = KernelRelease.ForArchitecture(release.ElfMachine == 62 ? "x86_64" : "aarch64");
        if (_bundledFallback && release == pinned)
        {
            progress?.Report(new("在线源不可用，使用内置内核 " + release.Version));
            try
            {
                return await BundledPackages.ExtractAsync(release.FileName, cached,
                    (path, token) => ValidateAsync(path, release, token), cancellation).ConfigureAwait(false);
            }
            catch (InvalidDataException) { failures.Add("内置内核校验失败"); }
        }
        throw new KernelAcquisitionException("在线及内置内核均不可用，可重试或选择离线包 " + release.FileName + "。\n" + string.Join("\n", failures));
    }

    public async Task<string> ImportAsync(string file, KernelRelease release, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(_directory);
        var staging = CachePath(release) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            progress?.Report(new("校验离线内核包 · " + release.FileName));
            await using (var input = File.OpenRead(file))
            await using (var output = File.Create(staging))
            {
                if (input.Length is <= 0 or > MaxPackageBytes) throw new InvalidDataException("内核包大小无效（上限 64 MiB）。");
                await BinaryUpload.CopyAsync(input, output, input.Length, null, cancellation).ConfigureAwait(false);
            }
            await ValidateAsync(staging, release, cancellation).ConfigureAwait(false);
            var target = CachePath(release);
            File.Move(staging, target, overwrite: true);
            return target;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static async Task DownloadAsync(HttpClient client, Uri uri, string path, string source, IProgress<DeploymentProgress>? progress, CancellationToken cancellation)
    {
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            if (uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new InvalidDataException("内核下载仅允许 HTTPS。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            headers.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                uri = new Uri(uri, response.Headers.Location ?? throw new InvalidDataException("内核下载重定向无效。"));
                continue;
            }
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total is <= 0 or > MaxPackageBytes) throw new InvalidDataException("内核包大小无效（上限 64 MiB）。");
            await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            await using var output = File.Create(path);
            var buffer = new byte[65536]; long received = 0;
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false);
                if (count == 0) break;
                received += count;
                if (received > MaxPackageBytes) throw new InvalidDataException("内核包超过 64 MiB。");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                if (timer.ElapsedMilliseconds >= 100) { progress?.Report(new("本机下载 · " + source, received, total)); timer.Restart(); }
            }
            if (received == 0 || (total.HasValue && received != total.Value)) throw new InvalidDataException("内核下载不完整。");
            progress?.Report(new("本机下载 · " + source, received, total));
            return;
        }
        throw new InvalidDataException("内核下载重定向过多。");
    }

    public static async Task ValidateAsync(string path, KernelRelease release, CancellationToken cancellation = default)
    {
        await using var input = File.OpenRead(path);
        if (input.Length is <= 0 or > MaxPackageBytes) throw new InvalidDataException("内核包大小无效（上限 64 MiB）。");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false));
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("内核包 SHA-256 不匹配，请使用 " + release.FileName + "。");
        input.Position = 0;
        using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
        var header = new byte[64];
        try { await gzip.ReadExactlyAsync(header, cancellation).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw new InvalidDataException("内核包缺少有效 ELF 文件头。"); }
        if (header[0] != 0x7f || header[1] != 'E' || header[2] != 'L' || header[3] != 'F' || header[4] != 2 || header[5] != 1
            || (header[16] != 2 && header[16] != 3) || header[17] != 0 || (header[18] | header[19] << 8) != release.ElfMachine)
            throw new InvalidDataException("内核不是目标服务器架构的 Linux 64 位程序。");
        var buffer = new byte[65536]; long unpacked = header.Length;
        int read;
        while ((read = await gzip.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            unpacked += read;
            if (unpacked > 150 * 1024 * 1024) throw new InvalidDataException("解压后的内核异常大。");
        }
    }
}
