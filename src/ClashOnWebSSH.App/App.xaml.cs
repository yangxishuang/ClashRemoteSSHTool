using System.IO;
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
            MessageBox.Show(Core.Redactor.Clean(args.Exception.Message), "Clash 遥控台", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var renderIndex = Array.IndexOf(e.Args, "--render-demo");
        var demo = e.Args.Contains("--demo") || renderIndex >= 0;
        Window window = demo && e.Args.Contains("--wizard") ? new DeploymentWindow(null, true, true) : new MainWindow(demo);
        MainWindow = window;
        if (renderIndex >= 0 && renderIndex + 1 < e.Args.Length)
        {
            window.ShowInTaskbar = false; window.ShowActivated = false;
            window.Left = -20000; window.Top = -20000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Show();
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                var visual = (FrameworkElement)window.Content;
                var tabIndex = Array.IndexOf(e.Args, "--tab");
                if (tabIndex >= 0 && tabIndex + 1 < e.Args.Length && int.TryParse(e.Args[tabIndex + 1], out var tab)
                    && window.FindName("MainTabs") is System.Windows.Controls.TabControl tabs)
                    tabs.SelectedIndex = Math.Clamp(tab, 0, 3);
                var busyIndex = Array.IndexOf(e.Args, "--busy-step");
                var step = busyIndex >= 0 && busyIndex + 1 < e.Args.Length && int.TryParse(e.Args[busyIndex + 1], out var parsedStep) ? Math.Clamp(parsedStep, 0, 3) : 0;
                var privilegeIndex = Array.IndexOf(e.Args, "--privilege");
                var privileges = privilegeIndex >= 0 && privilegeIndex + 1 < e.Args.Length && Enum.TryParse<Core.RemotePrivilegeMode>(e.Args[privilegeIndex + 1], true, out var parsedPrivilege)
                    ? parsedPrivilege : Core.RemotePrivilegeMode.PasswordRequired;
                if (window is DeploymentWindow wizard) wizard.PreviewOperation(step, privileges);
                void Capture()
                {
                    visual.UpdateLayout();
                    var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(e.Args[renderIndex + 1]))!);
                    using (var file = File.Create(e.Args[renderIndex + 1])) encoder.Save(file);
                    if (window is DeploymentWindow preview) preview.PreviewOperation(0, privileges);
                    window.Close(); Shutdown();
                }
                if (window is DeploymentWindow && step > 0)
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
