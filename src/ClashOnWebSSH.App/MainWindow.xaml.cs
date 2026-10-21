using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ClashOnWebSSH.Core;
using ClashOnWebSSH.App.Controls;

namespace ClashOnWebSSH.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsStore _settings = new();
    private readonly bool _demo;
    private RemoteActions? _remote;
    private bool _connected, _busy, _installed;
    private ProxyGroup? _group;
    private string _service = "尚未连接", _version = "—", _mode = "—", _port = "等待连接";
    private int _mixedPort = 7890;
    private string _status = "点击概览中的连接按钮，或打开 SSH 连接页。", _sample = "";
    private string _deployment = "连接后自动检查远端环境。已有代理直接管理，未安装时可在这里部署。";
    private string _logs = "连接服务器后，点击「读取最近 50 行」查看服务日志。";
    private readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public ObservableCollection<ProxyGroup> Groups { get; } = new();
    public ObservableCollection<NodeRow> Nodes { get; } = new();
    public ICollectionView NodesView { get; }
    public bool IsIdle => !_busy;
    public bool CanOperate => !_busy && (_remote != null || _demo);
    public bool CanDeploy => CanOperate && !_installed;
    public bool CanSelectNode => CanOperate && _group?.Type == "Selector";
    public bool CanEditConnection => !_busy && !_connected;
    public string ConnectionText => _demo ? "界面预览 · 演示数据" : _connected ? "SSH 已连接" : "尚未连接";
    public Brush ConnectionBrush => ColorBrush(_connected ? "#51D6BA" : "#7888A4");
    public string ServiceText => _service;
    public Brush ServiceBrush => ColorBrush(_service == "运行中" ? "#249C84" : "#8793A8");
    public string ServiceHint => _installed ? "systemd 用户服务" : "连接后检查安装状态";
    public string VersionText => _version;
    public string GroupCountText => _group?.Name ?? "—";
    public string NodeCountText => _group == null ? "请选择分组" : $"{Nodes.Count} 个成员 · {_group.Type}";
    public string ModeText => _mode;
    public string PortText => _port;
    public string HeaderBadge => _demo ? "界面预览 · 演示数据" : _connected ? $"SSH / {HostBox.Text.Trim()}" : "本机 EXE · SSH 远控";
    public string CurrentNodeText => _group == null ? "选择分组后查看实际节点；测速从远端服务器发起。" : "当前选择  ·  " + _group.Current;
    public string DeploymentHint => _deployment;
    public string RemoteLogText => _logs;
    public string StatusText => _status;
    public string SampleText => _sample;
    public Visibility BusyVisibility => _busy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyVisibility => Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow(bool demo = false)
    {
        _demo = demo;
        if (demo) { _maintenancePrivileges = RemotePrivilegeMode.PasswordRequired; _maintenancePrivilegesChecked = true; }
        NodesView = CollectionViewSource.GetDefaultView(Nodes);
        NodesView.Filter = item => item is NodeRow row && (string.IsNullOrWhiteSpace(SearchBox?.Text) || row.Name.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
        InitializeComponent();
        InAppFeedback.Attach(this);
        DataContext = this;
        var profile = _settings.Load();
        HostBox.Text = profile.Host; UserBox.Text = profile.Username; PortBox.Text = profile.Port.ToString();
        IdentityBox.Text = profile.IdentityFile; ServiceBox.Text = profile.ServiceName; ConfigBox.Text = profile.ConfigPath;
        ApiPortBox.Text = profile.ControllerPort.ToString(); AuthBox.SelectedIndex = (int)profile.Authentication;
        if (demo) Loaded += (_, _) => LoadDemo();
        Closing += (_, e) =>
        {
            if (_busy)
            { e.Cancel = true; InAppFeedback.Show(this, "操作正在执行，请等待结果后关闭窗口。", NoticeKind.Information); return; }
            _remote?.Dispose(); _remote = null;
            LoginPassword.Clear(); SudoPassword.Clear();
        };
    }

    private static Brush ColorBrush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    private void SetStatus(string value) { _status = Redactor.Clean(value); Notify(); }
    private void AddLog(string value)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {Redactor.Clean(value)}";
        _logs = Redactor.Clean(_logs + "\n" + line);
        Notify();
    }

    private ConnectionProfile ReadProfile()
    {
        if (!int.TryParse(PortBox.Text, out var port) || !int.TryParse(ApiPortBox.Text, out var apiPort)) throw new ArgumentException("请输入有效的端口数字。");
        var profile = new ConnectionProfile
        {
            Host = HostBox.Text.Trim(), Username = UserBox.Text.Trim(), Port = port,
            Authentication = (AuthenticationMode)AuthBox.SelectedIndex,
            IdentityFile = IdentityBox.Text.Trim(), ServiceName = ServiceBox.Text.Trim(),
            ConfigPath = ConfigBox.Text.Trim(), ControllerPort = apiPort
        };
        profile.Validate(); return profile;
    }

    internal async Task Run(string title, Func<Task> operation, Func<string>? successText = null)
    {
        if (_busy) return;
        if (_demo) { InAppFeedback.Show(this, "当前是界面演示，不会连接或修改任何服务器。"); return; }
        InAppFeedback.Dismiss(this);
        _busy = true; SetStatus(title + "…"); Notify();
        try
        {
            await operation();
            var message = successText?.Invoke() ?? title + "完成";
            SetStatus(message); AddLog(message);
            InAppFeedback.Show(this, message, NoticeKind.Success);
        }
        catch (Exception exception)
        {
            var message = Redactor.Clean(exception.Message);
            SetStatus(title + "失败：" + message); AddLog(title + "失败：" + message);
            InAppFeedback.Show(this, title + "失败：" + message, NoticeKind.Error);
        }
        finally { _busy = false; Notify(); }
    }

    private Task<bool> ConfirmAsync(string message, string title = "确认操作") => InAppFeedback.ConfirmAsync(this, message, title);

    private void AuthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordPanel != null) PasswordPanel.Visibility = AuthBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ConnectClicked(object sender, RoutedEventArgs e)
    {
        if (_connected && !_demo)
        {
            _remote?.Dispose(); _remote = null; _connected = false;
            _maintenancePrivilegesChecked = false; SudoPassword.Clear();
            _service = "已断开"; _version = "—"; _mode = "—"; _port = "连接已关闭";
            Groups.Clear(); Nodes.Clear(); Configurations.Clear(); _group = null; _networkKnown = false;
            ConnectButton.Content = "连接服务器"; SetStatus("已断开 SSH；远端代理继续运行。"); Notify(); return;
        }
        await Run("连接服务器", async () =>
        {
            var profile = ReadProfile();
            ISshTransport transport;
            if (profile.Authentication == AuthenticationMode.Password)
            {
                var trust = new HostKeyStore(_settings.DirectoryPath);
                transport = new PasswordSshTransport(profile, LoginPassword.Password, identity => trust.Verify(identity, host =>
                    Dispatcher.InvokeAsync(() => ConfirmAsync($"首次连接 {host.Host}:{host.Port}\n\n{host.Algorithm}\n{host.Fingerprint}\n\n请与服务器可信指纹核对。确认信任该主机？", "确认 SSH 主机身份")).Task.Unwrap().GetAwaiter().GetResult()));
            }
            else transport = new OpenSshTransport(profile);
            var actions = new RemoteActions(profile, transport);
            try
            {
                var capabilities = await actions.CheckConnectionAsync();
                _pythonAvailable = capabilities.Contains("python3");
                _remote = actions; _connected = true;
                await RefreshMaintenancePrivilegesAsync();
                _settings.Save(profile);
                ConnectButton.Content = "断开连接";
                if (!capabilities.Contains("python3"))
                {
                    _installed = capabilities.Contains("CLASHSSH_SERVICE=loaded"); _service = "需要准备";
                    _deployment = "SSH 已连通，但远端缺少 Python 3。请先点击准备依赖，再刷新环境。";
                    MainTabs.SelectedIndex = 3; Notify();
                }
                else await RefreshState();
            }
            catch
            {
                actions.Dispose(); _remote = null; _connected = false; ConnectButton.Content = "连接服务器";
                throw;
            }
            finally { LoginPassword.Clear(); }
        });
        if (_connected && !_installed && !_demo) await ShowDeploymentWizard();
    }

    private static string Text(JsonElement element, string name, string fallback = "") =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private async Task RefreshState()
    {
        if (_remote == null) return;
        var reply = await _remote.InvokeAsync("probe");
        var data = reply.Data;
        _pythonAvailable = true; _yamlAvailable = Flag(data, "yaml");
        _installed = Flag(data, "installed");
        _service = !_installed ? "未部署" : Text(data, "service") switch { "active" => "运行中", "inactive" => "已停止", "failed" => "启动失败", _ => "待检查" };
        _version = Text(data, "version", "—"); if (_version.Length == 0) _version = "—";
        _mode = Text(data, "mode") switch { "rule" => "规则分流", "global" => "全局代理", "direct" => "直连", _ => "—" };
        var port = data.TryGetProperty("mixedPort", out var portValue) ? portValue.GetInt32() : 7890;
        _mixedPort = port;
        _port = $"远端 127.0.0.1:{port}";
        _sample = "最近读取 " + DateTime.Now.ToString("HH:mm:ss");
        _deployment = _installed
            ? $"当前服务 {ServiceBox.Text} · Python {Text(data, "python")} · YAML {(Flag(data, "yaml") ? "就绪" : "缺失")} · 用户服务 {(Flag(data, "systemdUser") ? "可用" : "不可用")} · linger {(Flag(data, "linger") ? "已开启" : "未开启")}"
            : $"远端尚未检测到 Clash。{Text(data, "platform")} / {Text(data, "architecture")} · YAML {(Flag(data, "yaml") ? "就绪" : "需准备")} · 用户服务 {(Flag(data, "systemdUser") ? "可用" : "需检查")}。填入订阅后可部署。";
        if (Flag(data, "apiAvailable"))
        {
            var groups = await _remote.InvokeAsync("groups");
            ApplyGroups(groups.Data);
        }
        else
        {
            Groups.Clear(); Nodes.Clear(); _group = null;
            if (!_installed) MainTabs.SelectedIndex = 3;
        }
        try { await RefreshProfiles(); }
        catch (Exception error) { AddLog("配置列表读取失败：" + Redactor.Clean(error.Message)); }
        await RecheckNetwork();
        Notify();
    }

    private void GroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(_group, GroupBox.SelectedItem)) return;
        _group = GroupBox.SelectedItem as ProxyGroup;
        Nodes.Clear();
        if (_group != null)
            foreach (var member in _group.Members)
                Nodes.Add(new NodeRow(member.Name, member.Type, member.Name == _group.Current));
        Notify();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => NodesView?.Refresh();
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await Run("刷新远端状态", RefreshState);

    private async void UseNodeClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not NodeRow row || _group == null) return;
        var group = _group.Name;
        await Run("切换节点", async () => { await _remote!.InvokeAsync("select", new { group, node = row.Name }); await RefreshState(); });
    }
    private async Task RunDelay(object target)
    {
        var reply = await _remote!.InvokeAsync("delay", target);
        foreach (var result in reply.Data.GetProperty("results").EnumerateArray())
        {
            var row = Nodes.FirstOrDefault(n => n.Name == Text(result, "name"));
            if (row == null) continue;
            row.SetDelay(result.TryGetProperty("delay", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt32() : null);
        }
        AddLog("延迟测量由服务器发起，目标：www.gstatic.com/generate_204");
        _sample = "最近测速 " + DateTime.Now.ToString("HH:mm:ss"); Notify();
    }
    private async void NodeDelayClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is NodeRow row) await Run("单节点测速", () => RunDelay(new { node = row.Name }));
    }
    private async void GroupDelayClicked(object sender, RoutedEventArgs e)
    {
        if (_group != null) await Run("分组测速", () => RunDelay(new { group = _group.Name }));
    }
    private async void LogsClicked(object sender, RoutedEventArgs e) => await Run("读取服务日志", async () =>
    { var reply = await _remote!.InvokeAsync("logs", new { lines = 50 }); _logs = Redactor.Clean(Text(reply.Data, "text")); Notify(); });

    private async Task ServiceAction(string action)
    {
        if (action != "start" && !_demo && !await ConfirmAsync("这会影响服务器正在使用的代理连接。确认" + (action == "stop" ? "停止" : "重启") + "服务？")) return;
        await Run("服务操作", async () => { await _remote!.InvokeAsync("service", new { operation = action }); await RefreshState(); });
    }
    private async void StartClicked(object sender, RoutedEventArgs e) => await ServiceAction("start");
    private async void StopClicked(object sender, RoutedEventArgs e) => await ServiceAction("stop");
    private async void RestartClicked(object sender, RoutedEventArgs e) => await ServiceAction("restart");
    private void CopyProxyClicked(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText($"export http_proxy=http://127.0.0.1:{_mixedPort} https_proxy=http://127.0.0.1:{_mixedPort} all_proxy=socks5://127.0.0.1:{_mixedPort}");
        SetStatus("已复制；请粘贴到需要使用代理的远端终端执行。撤销：unset http_proxy https_proxy all_proxy");
    }
    private async void ConnectivityClicked(object sender, RoutedEventArgs e)
    {
        var summary = "远端代理测试完成";
        await Run("代理连通检查", async () =>
        {
            var result = await _remote!.InvokeAsync("connectivity");
            summary = $"远端代理测试完成 · HTTP {result.Data.GetProperty("httpStatus")} · {result.Data.GetProperty("elapsedMs")} ms · 目标：www.gstatic.com";
        }, () => summary);
    }

    private async Task Bootstrap(bool linger)
    {
        var title = linger ? "启用脱离登录运行" : "准备系统依赖";
        if (!_demo && !await ConfirmAsync(linger ? "将通过 loginctl 为当前 SSH 用户开启 linger，允许用户服务在退出后运行。需要 root 或 sudo。继续？" : "将在远端安装 Python 3、PyYAML 和 CA 证书。支持 apt/dnf/yum，需要 root 或 sudo。继续？", title)) return;
        await Run(title, async () =>
        {
            try { AddLog(await _remote!.BootstrapAsync(SudoPassword.Password, linger)); await RefreshMaintenancePrivilegesAsync(); await RefreshState(); }
            finally { SudoPassword.Clear(); }
        });
    }
    private async void BootstrapClicked(object sender, RoutedEventArgs e) => await Bootstrap(false);
    private async void LingerClicked(object sender, RoutedEventArgs e) => await Bootstrap(true);

    private void LoadDemo()
    {
        _connected = true; _installed = true; _service = "运行中"; _version = "v1.19.29";
        _mode = "规则分流"; _port = "远端 127.0.0.1:7890"; _sample = "演示数据 · 未连接服务器";
        _deployment = "界面演示：现有服务已安装。演示不会执行任何远端命令。";
        _pythonAvailable = true; _yamlAvailable = true;
        Configurations.Add(new() { Id = "demo1", Name = "宝可梦", Kind = "url", SourceHost = "subscription.example", Active = true, NodeCount = 112, UpdatedAt = DateTimeOffset.Now.AddHours(-3).ToUnixTimeSeconds(), Usage = new() { ["upload"] = 2147483648, ["download"] = 83966643404, ["total"] = 232787233177, ["expire"] = 0 } });
        Configurations.Add(new() { Id = "demo2", Name = "IPLC 国际流量", Kind = "url", SourceHost = "subscription.example", NodeCount = 68, UpdatedAt = DateTimeOffset.Now.AddHours(-3).ToUnixTimeSeconds(), Usage = new() { ["download"] = 314928476979, ["total"] = 1979120929996, ["expire"] = new DateTimeOffset(2027, 3, 27, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds() } });
        Configurations.Add(new() { Id = "demo3", Name = "备用配置", Kind = "file", SourceName = "backup.yaml", NodeCount = 12, UpdatedAt = DateTimeOffset.Now.ToUnixTimeSeconds() });
        var demo = new ProxyGroup { Name = "宝可梦", Type = "Selector", Current = "香港 · 专线 01", Members = new()
        {
            new() { Name = "自动选择", Type = "URLTest" }, new() { Name = "香港 · 专线 01", Type = "Shadowsocks" },
            new() { Name = "香港 · 直连 02", Type = "VLESS" }, new() { Name = "日本 · 东京 01", Type = "Trojan" },
            new() { Name = "新加坡 · 专线 01", Type = "VLESS" }, new() { Name = "台湾 · 家宽 02", Type = "Trojan" },
            new() { Name = "美国 · 洛杉矶 01", Type = "Shadowsocks" }, new() { Name = "德国 · 法兰克福 01", Type = "VLESS" }
        } };
        Groups.Add(demo); Groups.Add(new() { Name = "自动选择", Type = "URLTest", Current = demo.Current, Members = demo.Members });
        Groups.Add(new() { Name = "AI 服务", Type = "Selector", Current = demo.Current, Members = demo.Members });
        GroupBox.SelectedIndex = 0;
        var latency = new int?[] { 38, 42, 56, 93, 86, 65, 178, null };
        for (var i = 0; i < Nodes.Count; i++) Nodes[i].SetDelay(latency[i]);
        ConnectButton.Content = "演示模式";
        SetStatus("界面预览：以上为演示数据，未连接或修改服务器。");
    }
}

public sealed class ProxyGroup
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Current { get; set; } = "";
    public List<ProxyMember> Members { get; set; } = new();
}
public sealed class ProxyMember
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
}
public sealed class NodeRow : INotifyPropertyChanged
{
    public string Name { get; }
    public string Type { get; }
    public bool IsCurrent { get; }
    public string StatusText => IsCurrent ? "使用中" : "";
    public string DelayText { get; private set; } = "待测速";
    public NodeRow(string name, string type, bool current) { Name = name; Type = type; IsCurrent = current; }
    public void SetDelay(int? value) { DelayText = value.HasValue ? value + " ms" : "超时"; PropertyChanged?.Invoke(this, new(nameof(DelayText))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}
