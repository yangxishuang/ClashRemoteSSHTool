using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClashOnWebSSH.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            if (e.Args.Contains("--render-demo"))
            {
                var position = Array.IndexOf(e.Args, "--render-demo");
                if (position + 1 < e.Args.Length) File.WriteAllText(e.Args[position + 1] + ".error.txt", args.Exception.ToString());
                args.Handled = true; Shutdown(1); return;
            }
            var owner = Windows.OfType<Window>().FirstOrDefault(opened => opened.IsActive) ?? MainWindow;
            if (owner != null) Controls.InAppFeedback.Show(owner, args.Exception.Message, Controls.NoticeKind.Error);
            args.Handled = true;
        };
        var renderIndex = Array.IndexOf(e.Args, "--render-demo");
        var demo = e.Args.Contains("--demo") || renderIndex >= 0;
        if (demo) Themes.Appearance.SetDark(e.Args.Contains("--dark"), false); else Themes.Appearance.Load();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            Themes.Appearance.UpdateTitleBars();
            if (sender is Window opened && SystemParameters.ClientAreaAnimation)
                opened.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, Controls.Motion.Fast));
        }));
        Window window = demo && e.Args.Contains("--wizard") ? new DeploymentWindow(null, true, true)
            : demo && e.Args.Contains("--import") ? new ProfileImportWindow()
            : demo && e.Args.Contains("--uninstall") ? new UninstallWindow(JsonSerializer.SerializeToElement(new
            {
                service = "clash-verge.service", retainedBinary = "/usr/bin/verge-mihomo",
                files = new[] { new { label = "用户服务（演示）", path = "~/.config/systemd/user/clash-verge.service" }, new { label = "运行配置（演示）", path = "~/.config/clash-verge/config.yaml" } }
            })) : new MainWindow(demo);
        MainWindow = window;
        if (renderIndex >= 0 && renderIndex + 1 < e.Args.Length)
        {
            window.ShowInTaskbar = false; window.ShowActivated = false;
            window.Left = -20000; window.Top = -20000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Show();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var tabIndex = Array.IndexOf(e.Args, "--tab");
                if (tabIndex >= 0 && tabIndex + 1 < e.Args.Length && int.TryParse(e.Args[tabIndex + 1], out var tab)
                    && (window.FindName("MainTabs") ?? window.FindName("ImportTabs")) is System.Windows.Controls.TabControl tabs)
                    tabs.SelectedIndex = Math.Clamp(tab, 0, tabs.Items.Count - 1);
                var busyIndex = Array.IndexOf(e.Args, "--busy-step");
                var step = busyIndex >= 0 && busyIndex + 1 < e.Args.Length && int.TryParse(e.Args[busyIndex + 1], out var parsedStep) ? Math.Clamp(parsedStep, 0, 3) : 0;
                var privilegeIndex = Array.IndexOf(e.Args, "--privilege");
                var privileges = privilegeIndex >= 0 && privilegeIndex + 1 < e.Args.Length && Enum.TryParse<Core.RemotePrivilegeMode>(e.Args[privilegeIndex + 1], true, out var parsedPrivilege)
                    ? parsedPrivilege : Core.RemotePrivilegeMode.PasswordRequired;
                if (window is DeploymentWindow wizard)
                {
                    wizard.PreviewOperation(step, privileges);
                    if (step == 3) wizard.PreviewDeployment(new("SSH 上传内核", 12 * 1048576, 24 * 1048576), false);
                    if (e.Args.Contains("--offline-fallback")) wizard.PreviewDeployment(new("自动获取内核失败"), true);
                }
                if (window is MainWindow main && e.Args.Contains("--collapsed"))
                    ((System.Windows.Controls.Button)main.FindName("SidebarToggleButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                if (window is MainWindow network) network.PreviewNetwork(e.Args.Contains("--disconnected"));
                if (window is MainWindow scrolled && e.Args.Contains("--network")) ((System.Windows.Controls.ScrollViewer)scrolled.FindName("OverviewScroll")).ScrollToVerticalOffset(290);
                if (e.Args.Contains("--notice-success")) Controls.InAppFeedback.Show(window, "安装并启动成功；尚未启用 linger，请在向导中配置脱离登录运行。", Controls.NoticeKind.Success);
                if (e.Args.Contains("--confirm-demo")) _ = Controls.InAppFeedback.ConfirmAsync(window, "首次连接，请核对可信主机指纹。\nSHA256:示例指纹", "确认 SSH 主机身份");
                void Capture()
                {
                    var visual = (FrameworkElement)window.Content;
                    visual.UpdateLayout();
                    var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(e.Args[renderIndex + 1]))!);
                    using (var file = File.Create(e.Args[renderIndex + 1])) encoder.Save(file);
                    if (window is DeploymentWindow preview) preview.PreviewOperation(0, privileges);
                    window.Close(); Shutdown();
                }
                if (window is DeploymentWindow && step > 0 || window is MainWindow || e.Args.Contains("--notice-success") || e.Args.Contains("--confirm-demo"))
                {
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    timer.Tick += (_, _) => { timer.Stop(); Capture(); };
                    timer.Start();
                }
                else Capture();
            });
        }
        else window.Show();
    }
}
