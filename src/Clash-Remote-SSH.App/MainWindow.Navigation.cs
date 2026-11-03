using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClashRemoteSsh.App.Controls;
using ClashRemoteSsh.App.Themes;

namespace ClashRemoteSsh.App;

public partial class MainWindow
{
    private bool _sidebarExpanded = true;
    private int _previousPage = 5;
    public string AppVersion => "v" + Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
    public string ConnectedHost => _connected ? HostBox.Text.Trim() : "尚未连接服务器";
    public Visibility SidebarTextVisibility => _sidebarExpanded ? Visibility.Visible : Visibility.Collapsed;
    public Thickness SidebarInsets => new(_sidebarExpanded ? 12 : 4, 20, _sidebarExpanded ? 12 : 4, 18);
    public Thickness SidebarItemPadding => new(_sidebarExpanded ? 13 : 8, 0, _sidebarExpanded ? 13 : 8, 0);
    public Thickness SidebarHeaderMargin => new(_sidebarExpanded ? 3 : 0, 0, 0, 24);
    public string PageTitle => (MainTabs?.SelectedIndex ?? 4) switch
    { 0 => "代理节点", 1 => "配置", 2 => "运行日志", 3 => "服务管理", 5 => "SSH 连接", _ => "概览" };
    public string PageSubtitle => (MainTabs?.SelectedIndex ?? 4) switch
    {
        0 => "选择分组，查看节点并从远端测量延迟。",
        1 => "订阅链接与 YAML 文件，统一管理和切换。",
        2 => "按需读取远端服务日志。",
        3 => "管理远端用户服务与部署环境。",
        5 => "连接一台服务器，开始管理远端代理。",
        _ => "远端代理的当前状态，尽在这里。"
    };
    private void NavigateClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && int.TryParse(element.Tag?.ToString(), out var page)) MainTabs.SelectedIndex = page;
    }
    private void PageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs) || MainTabs.SelectedIndex < 0) return;
        var page = MainTabs.SelectedIndex;
        if (NavigationItems != null)
        {
            var ordinal = 0;
            foreach (var item in NavigationItems.Children.OfType<RadioButton>())
            {
                item.IsChecked = item.Tag?.ToString() == page.ToString();
                if (item.IsChecked == true && NavigationIndicator?.RenderTransform is TranslateTransform position)
                    position.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(ordinal * 48 + 12, Motion.Indicator) { EasingFunction = Motion.EaseOut() });
                ordinal++;
            }
        }
        if (IsLoaded && MainTabs.Template.FindName("PageContent", MainTabs) is ContentPresenter host)
        {
            var order = new[] { 4, 5, 0, 1, 2, 3 };
            var direction = Array.IndexOf(order, page) >= Array.IndexOf(order, _previousPage) ? 1 : -1;
            var transform = host.RenderTransform as TranslateTransform;
            if (transform is null || transform.IsFrozen) host.RenderTransform = transform = new TranslateTransform();
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(direction * Math.Min(180, host.ActualWidth * 0.25), 0, Motion.Page) { EasingFunction = Motion.EaseOut() });
            host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Common) { EasingFunction = Motion.EaseOut() });
        }
        _previousPage = page; Notify();
    }
    private void ToggleSidebarClicked(object sender, RoutedEventArgs e)
    {
        _sidebarExpanded = !_sidebarExpanded;
        NavigationPane.BeginAnimation(WidthProperty, new DoubleAnimation(_sidebarExpanded ? 220 : 48, Motion.Sidebar) { EasingFunction = Motion.EaseOut() });
        Notify();
    }
    private void ToggleThemeClicked(object sender, RoutedEventArgs e)
    { Appearance.SetDark(!Appearance.IsDark); foreach (var site in Sites) site.Refresh(); Notify(); }
}
