using ClashOnWebSSH.Core;

namespace ClashOnWebSSH.App;

internal static class ProfileTransfer
{
    public static async Task<object> PrepareAsync(ProfileImportData input)
    {
        var content = input.ContentBase64;
        var userinfo = "";
        if (input.Kind == "url")
        {
            var download = await new SubscriptionDownloader().DownloadAsync(input.Url);
            content = download.ContentBase64; userinfo = download.SubscriptionUserinfo;
        }
        return new { name = input.Name, kind = input.Kind, url = input.Url, contentBase64 = content, fileName = input.FileName, subscriptionUserinfo = userinfo };
    }
}
