using System.Net;

namespace ClashOnWebSSH.Core;

public sealed record DownloadedSubscription(string ContentBase64, string SubscriptionUserinfo, string SuggestedName = "");

public sealed class SubscriptionDownloader(HttpMessageHandler? testHandler = null)
{
    private const int Limit = 10 * 1024 * 1024;
    private static Uri Validate(Uri uri)
    {
        if (uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.AbsoluteUri.Length > 4096)
            throw new ArgumentException("订阅地址及重定向必须是无用户名密码前缀的 HTTPS 链接。");
        return uri;
    }

    public async Task<DownloadedSubscription> DownloadAsync(string url, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)) throw new ArgumentException("请输入有效的 HTTPS 订阅地址。");
        Validate(target);
        for (var attempt = 0; ; attempt++)
        {
            using var handler = testHandler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false, UseProxy = attempt > 0,
                AutomaticDecompression = DecompressionMethods.All
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(attempt == 0 ? 25 : 45) };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(attempt == 0 ? 60 : 90));
            try { return await ReadAsync(client, target, deadline.Token); }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
            {
                cancellation.ThrowIfCancellationRequested();
                if (testHandler != null || attempt > 0) throw new InvalidOperationException("订阅下载失败，请检查本机网络、系统代理和订阅站连通性。");
            }
        }
    }

    private static async Task<DownloadedSubscription> ReadAsync(HttpClient client, Uri target, CancellationToken cancellation)
    {
        var source = target;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Validate(target));
            request.Headers.UserAgent.ParseAdd("clash-verge/v2.5.2");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location == null) throw new InvalidOperationException("订阅站重定向缺少目标地址。");
                target = Validate(new Uri(target, response.Headers.Location)); continue;
            }
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("订阅下载失败，HTTP " + (int)response.StatusCode + "。请检查链接和订阅状态。");
            if (response.Content.Headers.ContentLength > Limit) throw new InvalidOperationException("订阅超过10MiB。");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            using var content = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
            {
                if (content.Length + count > Limit) throw new InvalidOperationException("订阅超过10MiB。");
                content.Write(buffer, 0, count);
            }
            var userinfo = response.Headers.TryGetValues("subscription-userinfo", out var values) ? string.Join(";", values) : "";
            return new(Convert.ToBase64String(content.ToArray()), userinfo[..Math.Min(userinfo.Length, 2048)], SubscriptionNames.FromResponse(response, source));
        }
        throw new InvalidOperationException("订阅站重定向次数过多。");
    }
}
