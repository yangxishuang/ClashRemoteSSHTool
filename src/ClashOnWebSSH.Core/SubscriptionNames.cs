using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace ClashOnWebSSH.Core;

public static class SubscriptionNames
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static string FromResponse(HttpResponseMessage response, Uri source)
    {
        if (response.Headers.TryGetValues("profile-title", out var titles))
        {
            var title = titles.FirstOrDefault() ?? "";
            if (title.Length <= 2048)
            {
                if (title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
                {
                    try { title = StrictUtf8.GetString(Convert.FromBase64String(title[7..].Trim())); }
                    catch (Exception error) when (error is FormatException or DecoderFallbackException) { title = ""; }
                }
                var name = Clean(title, false);
                if (name.Length > 0) return name;
            }
        }
        if (response.Content.Headers.TryGetValues("content-disposition", out var values))
        {
            var header = values.FirstOrDefault() ?? "";
            if (header.Length <= 2048 && ContentDispositionHeaderValue.TryParse(header, out var disposition))
            {
                foreach (var value in new[] { disposition.FileNameStar, disposition.FileName })
                {
                    var name = Clean(value ?? "", true);
                    if (name.Length > 0) return name;
                }
            }
        }
        // URL paths and queries often contain subscription credentials, so never use them as names.
        return Clean(source.IdnHost, false);
    }

    public static string Resolve(string custom, string suggested, string url = "", string fileName = "")
    {
        if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
        var name = Clean(suggested, false);
        if (name.Length == 0 && fileName.Length > 0) name = Clean(fileName, true);
        if (name.Length == 0 && Uri.TryCreate(url, UriKind.Absolute, out var uri)) name = Clean(uri.IdnHost, false);
        return name.Length > 0 ? name : "未命名配置";
    }

    private static string Clean(string value, bool fileName)
    {
        value = value.Trim().Trim('"', '\'');
        try { value = Uri.UnescapeDataString(value); } catch (UriFormatException) { return ""; }
        // HTTP implementations may expose UTF-8 filename bytes as Latin-1 characters.
        if (value.Any(c => c > 127) && value.All(c => c <= 255))
        {
            try { value = StrictUtf8.GetString(Encoding.Latin1.GetBytes(value)); } catch (DecoderFallbackException) { }
        }
        if (fileName) value = value.Replace('\\', '/').Split('/').Last();
        value = Regex.Replace(value, @"(?i)\.ya?ml$", "");
        value = Regex.Replace(value, @"[\p{Cc}\u202A-\u202E\u2066-\u2069]", " ").Trim();
        if (value.Contains("://", StringComparison.Ordinal)) return "";
        value = string.Concat(value.EnumerateRunes().Take(80).Select(rune => rune.ToString()));
        return value is "." or ".." ? "" : value;
    }
}
