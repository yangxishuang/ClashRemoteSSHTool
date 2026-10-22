using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClashOnWebSSH.App.Controls;
using ClashOnWebSSH.Core;

namespace ClashOnWebSSH.App;

public partial class MainWindow
{
    private bool _networkKnown, _networkApiAvailable, _systemProxyEnabled, _proxySupported, _tunEnabled, _tunConfigured, _tunReady, _tunPending, _tunDevice, _tunRunning;
    private string _tunReason = "连接服务器后检查设备与权限。", _networkOperation = "";
    private bool _takeoverKnown, _bothOff, _directConfirmed;
    private string _takeoverMode = "";
    private bool _testingSites;
    private int _previousNetworkTab;
    private void NetworkTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs || !ReferenceEquals(e.Source, tabs) || tabs.SelectedIndex < 0) return;
        SlideTabContent(tabs, _previousNetworkTab);
        _previousNetworkTab = tabs.SelectedIndex;
    }
    private static void SlideTabContent(TabControl tabs, int previous)
    {
        if (tabs.IsLoaded && tabs.Template.FindName("SwitchContent", tabs) is ContentPresenter host)
        {
            var direction = tabs.SelectedIndex >= previous ? 1 : -1;
            var transform = host.RenderTransform as TranslateTransform;
            if (transform is null || transform.IsFrozen) host.RenderTransform = transform = new TranslateTransform();
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(direction * host.ActualWidth, 0, Motion.Common) { EasingFunction = Motion.EaseOut() });
            host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Fast));
        }
    }
    public bool IsNetworkChanging => _busy && !_testingSites && _networkOperation.Length > 0;
    public bool IsSitesTesting => _busy && _testingSites;
    public ObservableCollection<WebsiteRow> Sites { get; } = new([new("chatgpt", "ChatGPT", "✳"), new("github", "GitHub", "GH"), new("google", "Google", "G"), new("grok", "Grok", "𝕏")]);
    public bool SystemProxyEnabled => _networkKnown && _systemProxyEnabled;
    public bool TunEnabled => _networkKnown && (_tunEnabled || _tunConfigured);
    public bool CanReadNetwork => CanOperate && _connected;
    public bool CanChangeSystemProxy => CanReadNetwork && _networkKnown && _proxySupported && !_tunPending && (_installed ? _networkApiAvailable : _systemProxyEnabled);
    public bool CanChangeTun => CanReadNetwork && _networkKnown && _networkApiAvailable && !_tunPending && (_tunEnabled || _tunConfigured || _tunReady || CanPrepareTun);
    public bool CanPrepareTun => CanReadNetwork && _networkKnown && _tunDevice && _tunRunning && !_tunPending;
    public bool CanTestSites => CanReadNetwork && _installed && _networkKnown && _networkApiAvailable && _tunRunning;
    public string WebsiteTestHint => _networkKnown && _directConfirmed ? "从服务器直连测量 HTTPS 响应时间" : "从服务器按当前网络设置测量 HTTPS 响应时间";
    public Visibility ConnectPromptVisibility => _connected ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NetworkOperationVisibility => _networkOperation.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string NetworkOperationText => _networkOperation;
    public Brush ServiceSurface => (Brush)FindResource(_service == "运行中" ? "SuccessSurface" : "Block");
    public string SystemProxyHint => !_connected ? "新终端自动配置；双关时旧终端也可直连。" : !_networkKnown ? "尚未读取状态，请刷新。" : !_proxySupported ? "当前登录 shell 不支持自动加载。" : _installed && !_networkApiAvailable ? "请先启动服务或恢复管理接口，再切换。" : _systemProxyEnabled ? "已开启 · 新终端自动使用远端代理。" : "已关闭 · 关闭最后一个入口时恢复直连。";
    public string TunHint => !_connected ? "连接服务器后检查设备与权限。" : !_networkKnown ? "状态未确认，请刷新检查。" : _tunPending ? "正在确认 SSH 连通；失败将在约 75 秒后自动回退。" : _tunConfigured && !_tunEnabled && _networkApiAvailable ? "配置已开启但内核未启用，可关闭后恢复直连。" : _tunEnabled ? "已开启 · 内网与 SSH 客户端地址保持直连。" : _tunReason;

    internal void ApplyNetworkStatus(JsonElement data)
    {
        var proxy = data.GetProperty("systemProxy"); var tun = data.GetProperty("tun");
        _systemProxyEnabled = Flag(proxy, "enabled"); _proxySupported = Flag(proxy, "supported");
        _tunEnabled = Flag(tun, "enabled"); _tunConfigured = Flag(tun, "configured"); _tunReady = Flag(tun, "ready"); _tunPending = Flag(tun, "pending");
        _tunDevice = Flag(tun, "device"); _tunRunning = Flag(tun, "running");
        _networkApiAvailable = Text(tun, "apiError").Length == 0;
        _takeoverKnown = data.TryGetProperty("takeover", out var takeover);
        _bothOff = _takeoverKnown && Flag(takeover, "bothOff");
        _directConfirmed = _takeoverKnown && Flag(takeover, "directConfirmed");
        _takeoverMode = _takeoverKnown ? Text(takeover, "mode") : "";
        if (_networkApiAvailable && _takeoverKnown)
            _mode = _takeoverMode switch { "direct" => "直连", "global" => "全局代理", "rule" => "规则分流", _ => "—" };
        _tunReason = !_tunDevice ? "缺少 /dev/net/tun，需服务器管理员开放设备。"
            : !_tunRunning ? "请先启动代理服务。" : !Flag(tun, "capable") ? "需要管理员准备网络权限。"
            : !Flag(tun, "linger") ? "需要启用脱离登录运行，以便失联时自动回退。"
            : !Flag(tun, "timer") ? "服务器缺少 systemd-run，无法安排自动回退。"
            : "已关闭 · 设备和权限就绪。";
        if (Text(tun, "apiError").Length > 0) { _tunReady = false; _tunReason = "管理接口暂不可达，请先检查服务。"; }
        _networkKnown = true; SynchronizeProxyMode(); Notify();
    }
    private async Task RefreshNetwork()
    {
        if (_remote is null) { _networkKnown = false; SynchronizeProxyMode(); Notify(); return; }
        try { ApplyNetworkStatus((await _remote.InvokeAsync("network.status")).Data); }
        catch { _networkKnown = false; SynchronizeProxyMode(); Notify(); throw; }
    }
    private async Task RecheckNetwork(bool routing = false)
    {
        try
        {
            await RefreshNetwork();
            if (routing && _networkApiAvailable && _remote is not null)
            {
                ApplyGroups((await _remote.InvokeAsync("groups")).Data);
                await RefreshProfiles();
            }
        }
        catch (Exception error) { AddLog("网络状态未确认：" + Redactor.Clean(error.Message)); }
    }
    private async void RefreshNetworkClicked(object sender, RoutedEventArgs e) => await Run("刷新网络状态", RefreshNetwork);
    private async void SystemProxyClicked(object sender, RoutedEventArgs e) => await ChangeSystemProxy(((ToggleButton)sender).IsChecked == true);
    private async Task ChangeSystemProxy(bool enabled)
    {
        Notify();
        var message = "系统代理设置已确认";
        _networkOperation = "正在切换系统代理与直连状态…";
        try
        {
            await Run(enabled ? "开启系统代理" : "关闭系统代理并核验直连", async () =>
            {
                try { message = Text((await _remote!.InvokeAsync("network.system-proxy", new { enabled })).Data, "message", message); }
                finally { await RecheckNetwork(true); }
                if (!_networkKnown) throw new InvalidOperationException("命令已返回，但最新状态未能确认；请刷新后再操作。");
            }, () => message);
        }
        finally { _networkOperation = ""; Notify(); }
    }
    private async void TunClicked(object sender, RoutedEventArgs e)
    {
        var enabled = ((ToggleButton)sender).IsChecked == true;
        Notify();
        if (!CanChangeTun) return;
        if (_demo) { await Run("切换 TUN", () => Task.CompletedTask); return; }
        _networkOperation = "正在检查 TUN 设备与权限…";
        var message = "TUN 状态已确认";
        try
        {
            await Run("切换 TUN", async () =>
            {
                await RefreshNetwork();
                if (_tunPending) throw new InvalidOperationException("TUN 正等待连通确认或自动回退，请稍后刷新。");
                if (!_networkApiAvailable) throw new InvalidOperationException("管理接口暂不可达，请先检查服务。");
                if (enabled && !_tunReady)
                {
                    if (!_tunDevice || !_tunRunning) throw new InvalidOperationException(TunHint);
                    _networkOperation = "需要先准备 TUN 权限…"; Notify();
                    var preparation = new TunPreparationWindow(_remote!, continueToEnable: true) { Owner = this, ShowInTaskbar = false };
                    await Task.Yield();
                    preparation.ShowDialog();
                    await RecheckNetwork(true);
                    if (!preparation.Prepared) { message = "已取消 TUN 权限准备"; return; }
                    if (!_networkKnown || !_tunReady) throw new InvalidOperationException("准备后的 TUN 条件未确认，请刷新核对；未提交开启。");
                }
                _networkOperation = enabled ? "正在开启 TUN 并重新确认 SSH 连通…" : "正在关闭 TUN 并核验状态…"; Notify();
                try { message = Text((await _remote!.SetTunAsync(enabled)).Data, "message", message); }
                finally { await RecheckNetwork(true); }
                if (!_networkKnown || _tunPending || _tunEnabled != enabled)
                    throw new InvalidOperationException("TUN 最新状态未能确认，请刷新核对；不要重复提交。");
            }, () => message);
        }
        finally { _networkOperation = ""; Notify(); }
    }
    private async void TestSitesClicked(object sender, RoutedEventArgs e)
    {
        var site = (sender as Button)?.Tag?.ToString() ?? "all";
        var selected = Sites.Where(row => site == "all" || row.Id == site).ToList();
        if (!CanTestSites || selected.Count == 0) return;
        foreach (var row in selected) row.Start();
        _testingSites = true;
        _networkOperation = "正在按服务器当前网络设置测试网站 HTTPS 响应…";
        try
        {
            await Run("网站测试", async () =>
            {
                var data = (await _remote!.InvokeAsync("network.test", new { site })).Data;
                foreach (var result in data.GetProperty("results").EnumerateArray())
                    Sites.Single(row => row.Id == Text(result, "id")).Apply(result);
            });
        }
        finally { foreach (var row in selected) row.Finish(); _networkOperation = ""; _testingSites = false; Notify(); }
    }
    internal void PreviewNetwork(bool disconnected = false)
    {
        if (!_demo) throw new InvalidOperationException("仅用于界面预览。");
        _connected = !disconnected;
        if (disconnected) { _service = "尚未连接"; _installed = false; _version = "—"; _mode = "—"; _port = "等待连接"; Groups.Clear(); Nodes.Clear(); _group = null; }
        else
        {
            ApplyNetworkStatus(JsonSerializer.SerializeToElement(new { systemProxy = new { enabled = true, supported = true }, takeover = new { bothOff = false, mode = "rule", directConfirmed = false }, tun = new { enabled = false, capable = false, device = true, linger = true, timer = true, running = true, ready = false } }));
            var delays = new[] { 468, 257, 192, 523 };
            for (var i = 0; i < Sites.Count; i++) Sites[i].Apply(JsonSerializer.SerializeToElement(new { delay = delays[i], httpStatus = 200, message = "连接正常 · 演示数据" }));
        }
        Notify();
    }
    internal void PreviewTakeover(bool restored)
    {
        if (!_demo) throw new InvalidOperationException("仅用于界面预览。");
        ApplyNetworkStatus(JsonSerializer.SerializeToElement(new { systemProxy = new { enabled = false, supported = true }, takeover = new { bothOff = true, mode = restored ? "direct" : "rule", directConfirmed = restored }, tun = new { enabled = false, capable = false, device = true, linger = true, timer = true, running = true, ready = false } }));
        if (restored) { Groups.Clear(); Nodes.Clear(); _group = null; }
        Notify();
    }
}

public sealed class WebsiteRow(string id, string name, string mark) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Mark { get; } = mark;
    private string _result = "测试", _detail = "尚未测试";
    private bool _pending, _measured, _failed;
    public string ResultText => _pending ? "测试中…" : _result;
    public string Detail => _detail;
    public Brush ResultBrush => (Brush)Application.Current.FindResource(_pending || !_measured ? "Accent" : _failed ? "Error" : "Success");
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new(null));
    public void Start() { _pending = true; _measured = false; _result = "重试"; _detail = "正在连接…"; Refresh(); }
    public void Apply(JsonElement result)
    {
        _measured = true; _pending = false;
        _failed = !result.TryGetProperty("delay", out var delay) || delay.ValueKind != JsonValueKind.Number;
        _result = _failed ? "重试" : delay.GetInt32() + " ms";
        _detail = result.GetProperty("message").GetString() ?? "";
        if (result.TryGetProperty("httpStatus", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() >= 400)
            _detail += " · HTTP " + code.GetInt32();
        Refresh();
    }
    public void Finish() { if (_pending) { _detail = "结果未获取，请重试"; _failed = true; _measured = true; } _pending = false; Refresh(); }
}
