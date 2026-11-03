using System.Diagnostics;
using System.Text;

namespace ClashRemoteSsh.Core;

internal static class BinaryUpload
{
    public static async Task CopyAsync(Stream source, Stream target, long length, IProgress<long>? progress, CancellationToken cancellation)
    {
        if (length is <= 0 or > KernelPackageStore.MaxPackageBytes) throw new ArgumentOutOfRangeException(nameof(length));
        var buffer = new byte[65536];
        long sent = 0;
        var timer = Stopwatch.StartNew();
        while (sent < length)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - sent)), cancellation).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("本机内核文件不完整。");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
            sent += count;
            if (timer.ElapsedMilliseconds >= 100 || sent == length) { progress?.Report(sent); timer.Restart(); }
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellation).ConfigureAwait(false) != 0)
            throw new InvalidDataException("本机内核文件大小发生变化。");
        await target.FlushAsync(cancellation).ConfigureAwait(false);
    }
}

public sealed partial class OpenSshTransport
{
    public async Task<CommandResult> UploadAsync(string command, ReadOnlyMemory<byte> header, Stream input, long length,
        IProgress<long>? progress, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var start = input.CanSeek ? input.Position : -1;
        for (var attempt = 0; ; attempt++)
        {
            if (attempt > 0) input.Position = start;
            var result = await UploadOnceAsync(command, header, input, length, progress, timeout, cancellation).ConfigureAwait(false);
            if (start < 0 || attempt >= 2 || result.ExitCode != 255 || !result.Error.Contains("kex_exchange_identification", StringComparison.Ordinal)) return result;
            await Task.Delay(TimeSpan.FromSeconds(2 + attempt), cancellation).ConfigureAwait(false);
        }
    }

    private async Task<CommandResult> UploadOnceAsync(string command, ReadOnlyMemory<byte> header, Stream input, long length,
        IProgress<long>? progress, TimeSpan timeout, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = BuildStartInfo(_profile, command) };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        budget.CancelAfter(timeout);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(budget.Token);
        var stderr = process.StandardError.ReadToEndAsync(budget.Token);
        try
        {
            IOException? writeError = null;
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(header, budget.Token).ConfigureAwait(false);
                await BinaryUpload.CopyAsync(input, process.StandardInput.BaseStream, length, progress, budget.Token).ConfigureAwait(false);
            }
            catch (IOException error) { writeError = error; }
            finally { try { process.StandardInput.Close(); } catch (IOException) { } }
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
            var result = new CommandResult(process.ExitCode, await stdout, await stderr);
            if (writeError != null && result.ExitCode == 0) throw writeError;
            return result;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}

public sealed partial class PasswordSshTransport
{
    public Task<CommandResult> UploadAsync(string command, ReadOnlyMemory<byte> header, Stream input, long length,
        IProgress<long>? progress, TimeSpan timeout, CancellationToken cancellation = default)
        => Task.Run(async () =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            budget.CancelAfter(timeout);
            await _serial.WaitAsync(budget.Token).ConfigureAwait(false);
            try
            {
                if (!_client.IsConnected)
                {
                    _hostError = null;
                    try { await _client.ConnectAsync(budget.Token).ConfigureAwait(false); }
                    catch (Exception error) when (_hostError != null) { throw new InvalidOperationException(_hostError, error); }
                }
                using var remote = _client.CreateCommand(command, Encoding.UTF8);
                remote.CommandTimeout = timeout;
                var execute = remote.ExecuteAsync(budget.Token);
                try
                {
                    using (var target = remote.CreateInputStream())
                    {
                        await target.WriteAsync(header, budget.Token).ConfigureAwait(false);
                        await BinaryUpload.CopyAsync(input, target, length, progress, budget.Token).ConfigureAwait(false);
                    } // EOF is required by the receiver before it commits the package.
                    await execute.ConfigureAwait(false);
                }
                catch
                {
                    remote.CancelAsync();
                    try { await execute.ConfigureAwait(false); } catch { }
                    throw;
                }
                return new CommandResult(remote.ExitStatus ?? -1, remote.Result, remote.Error);
            }
            finally { _serial.Release(); }
        }, cancellation);
}
