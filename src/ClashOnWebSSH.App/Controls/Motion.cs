using System.Windows;
using System.Windows.Media.Animation;

namespace ClashOnWebSSH.App.Controls;

public static class Motion
{
    public static Duration Fast => new(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 200 : 0));
    public static Duration Common => new(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 300 : 0));
    public static Duration Sidebar => new(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 250 : 0));
    public static Duration Indicator => new(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 320 : 0));
    public static Duration Page => new(TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 450 : 0));
    public static IEasingFunction EaseOut() => new CubicEase { EasingMode = EasingMode.EaseOut };
}
