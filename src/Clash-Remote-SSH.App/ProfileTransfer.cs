using ClashRemoteSsh.Core;

namespace ClashRemoteSsh.App;

internal static class ProfileTransfer
{
    public static async Task<object> PrepareAsync(ProfileImportData input, SubscriptionDownloader? downloader = null)
    {
        var content = input.ContentBase64;
        var userinfo = "";
        var suggested = "";
        if (input.Kind == "url")
        {
            var download = await (downloader ?? new SubscriptionDownloader()).DownloadAsync(input.Url);
            content = download.ContentBase64; userinfo = download.SubscriptionUserinfo;
            suggested = download.SuggestedName;
        }
        var name = SubscriptionNames.Resolve(input.Name, suggested, input.Url, input.FileName);
        return new { name, kind = input.Kind, url = input.Url, contentBase64 = content, fileName = input.FileName, subscriptionUserinfo = userinfo };
    }
}
