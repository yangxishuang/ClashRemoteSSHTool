using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClashOnWebSSH.App.Themes;

public static class Appearance
{
    public static bool IsDark { get; private set; }
    private static string PreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashOnWebSSH", "appearance.json");
    public static void Load()
    {
        try
        {
            if (File.Exists(PreferencePath)) SetDark(JsonSerializer.Deserialize<bool>(File.ReadAllText(PreferencePath)), false);
        }
        catch (Exception) { SetDark(false, false); }
    }
    public static void SetDark(bool dark, bool persist = true)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var theme = new ResourceDictionary { Source = new Uri($"/ClashOnWebSSH;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        if (dictionaries.Count == 0) dictionaries.Add(theme); else dictionaries[0] = theme;
        IsDark = dark;
        UpdateTitleBars();
        if (!persist) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, JsonSerializer.Serialize(dark));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    public static void UpdateTitleBars()
    {
        foreach (Window window in Application.Current.Windows)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) continue;
            var dark = IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
    }
}
