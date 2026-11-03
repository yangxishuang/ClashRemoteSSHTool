using System.Security.Cryptography;
using System.Text.Json;

namespace ClashRemoteSsh.Core;

internal static class BundledPackages
{
    public static async Task<string> ExtractAsync(string name, string target, Func<string, CancellationToken, Task> validate, CancellationToken cancellation)
    {
        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var input = typeof(BundledPackages).Assembly.GetManifestResourceStream("bundled." + name)
                ?? throw new InvalidDataException("内置资源缺失。");
            await using (var output = File.Create(staging))
                await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
            await validate(staging, cancellation).ConfigureAwait(false);
            File.Move(staging, target, overwrite: true);
            return target;
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}

public sealed record GeodataRelease(string Name, string Version, string Sha256, long Size)
{
    public static IReadOnlyList<GeodataRelease> All { get; } = Load();
    private static IReadOnlyList<GeodataRelease> Load()
    {
        using var stream = typeof(GeodataRelease).Assembly.GetManifestResourceStream("geodata-manifest.json")!;
        using var document = JsonDocument.Parse(stream);
        var version = document.RootElement.GetProperty("version").GetString()!;
        return document.RootElement.GetProperty("assets").EnumerateObject().Select(a => new GeodataRelease(a.Name, version,
            a.Value.GetProperty("sha256").GetString()!, a.Value.GetProperty("size").GetInt64())).ToArray();
    }
    public static GeodataRelease ForName(string name) => All.SingleOrDefault(a => a.Name == name)
        ?? throw new InvalidDataException("不支持的 GeoData 资源。");
    public async Task ValidateAsync(string path, CancellationToken cancellation = default)
    {
        await using var input = File.OpenRead(path);
        if (input.Length != Size || Size is <= 0 or > KernelPackageStore.MaxPackageBytes
            || !Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Name + " 大小或 SHA-256 不匹配。");
    }
}

/// <summary>Ship a pinned baseline so first-start validation never needs to fetch GEO databases.</summary>
public sealed class GeodataPackageStore(string? directory = null)
{
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashOnWebSSH", "geodata");
    public async Task<string> AcquireAsync(string name, IProgress<DeploymentProgress>? progress = null, CancellationToken cancellation = default)
    {
        var release = GeodataRelease.ForName(name);
        var folder = Path.Combine(_directory, release.Version); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, release.Name);
        if (File.Exists(path))
        {
            try { await release.ValidateAsync(path, cancellation).ConfigureAwait(false); return path; }
            catch (InvalidDataException) { }
        }
        progress?.Report(new("准备内置规则数据 · " + name));
        return await BundledPackages.ExtractAsync(release.Name, path, release.ValidateAsync, cancellation).ConfigureAwait(false);
    }
}
