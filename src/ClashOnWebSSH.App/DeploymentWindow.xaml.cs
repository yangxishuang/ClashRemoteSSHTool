using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using ClashOnWebSSH.Core;

namespace ClashOnWebSSH.App;

public partial class DeploymentWindow : Window, INotifyPropertyChanged
{
    private readonly RemoteActions _remote;
    private readonly bool _demo;
    private readonly string? _preferredProfileId;
    private bool _busy, _python, _yaml, _systemd, _installed, _linger;
    private string _status = "先检查依赖，再导入一个配置。";
    public ObservableCollection<ConfigurationProfile> Profiles { get; } = new();
    public bool IsIdle => !_busy;
    public bool CanImport => !_busy && _python && _yaml;
    public bool CanDeploy => CanImport && _systemd && !_installed;
    public string StatusText => _status;
    public string EnvironmentText => $"Python {(_python ? "就绪" : "缺失")} · YAML {(_yaml ? "就绪" : "缺失")} · 用户会话 {(_systemd ? "可用" : "待检查")} · linger {(_linger ? "已开启" : "未开启")}。准备依赖和 linger 需要 root / sudo。";
    public event PropertyChangedEventHandler? PropertyChanged;
    public DeploymentWindow(RemoteActions? remote, bool pythonAvailable, bool demo = false, string? preferredProfileId = null)
    {
        _remote = remote!; _python = pythonAvailable; _demo = demo; _preferredProfileId = preferredProfileId;
        InitializeComponent(); DataContext = this;
        Loaded += async (_, _) =>
        {
            if (_demo)
            {
                _python = true; _yaml = true; _systemd = true;
                Profiles.Add(new() { Id = "demo", Name = "IPLC 国际流量（演示）", Kind = "url" }); ProfileBox.SelectedIndex = 0;
                _status = "向导界面预览，不连接或修改服务器。"; Notify();
            }
            else if (_python) await RunTask("读取部署环境", RefreshAsync);
        };
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => SudoBox.Clear();
    }
    private void Notify() => PropertyChanged?.Invoke(this, new(null));
    private async Task RunTask(string title, Func<Task> operation)
    {
        if (_busy) return;
        if (_demo) { MessageBox.Show(this, "当前为向导演示，不执行服务器操作。", "界面预览"); return; }
        _busy = true; _status = title + "…"; Notify();
        try { await operation(); _status = title + "完成"; }
        catch (Exception error) { _status = Redactor.Clean(error.Message); }
        finally { _busy = false; Notify(); }
    }
    private async Task RefreshAsync()
    {
        var result = await _remote.InvokeAsync("probe");
        _python = true; _yaml = result.Data.GetProperty("yaml").GetBoolean();
        _systemd = result.Data.GetProperty("systemdUser").GetBoolean();
        _installed = result.Data.GetProperty("installed").GetBoolean(); _linger = result.Data.GetProperty("linger").GetBoolean();
        var previous = (ProfileBox.SelectedItem as ConfigurationProfile)?.Id ?? _preferredProfileId;
        var list = await _remote.InvokeAsync("profiles.list");
        var entries = JsonSerializer.Deserialize<List<ConfigurationProfile>>(list.Data.GetProperty("profiles").GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        Profiles.Clear(); foreach (var entry in entries.Where(p => p.IsStored)) Profiles.Add(entry);
        ProfileBox.SelectedItem = Profiles.FirstOrDefault(p => p.Id == previous) ?? Profiles.FirstOrDefault(); Notify();
    }
    private async void PrepareClicked(object sender, RoutedEventArgs e) => await RunTask("准备系统依赖", async () =>
    {
        try { await _remote.BootstrapAsync(SudoBox.Password, false); _python = true; await RefreshAsync(); }
        finally { SudoBox.Clear(); }
    });
    private async void LingerClicked(object sender, RoutedEventArgs e) => await RunTask("启用脱离登录运行", async () =>
    {
        try { await _remote.BootstrapAsync(SudoBox.Password, true); if (_python) await RefreshAsync(); }
        finally { SudoBox.Clear(); }
    });
    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileImportWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } input) return;
        await RunTask("导入配置", async () =>
        {
            var payload = await ProfileTransfer.PrepareAsync(input);
            var result = await _remote.InvokeAsync("profiles.import", payload);
            await RefreshAsync();
            var identity = result.Data.GetProperty("profile").GetProperty("id").GetString();
            ProfileBox.SelectedItem = Profiles.FirstOrDefault(p => p.Id == identity);
        });
    }
    private async void DeployClicked(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ConfigurationProfile profile) { _status = "请先添加或选择一个配置。"; Notify(); return; }
        await RunTask("部署并启动（下载可能需要几分钟）", async () =>
        {
            var result = await _remote.InvokeAsync("deploy", new { profileId = profile.Id });
            await RefreshAsync();
            MessageBox.Show(this, result.Data.GetProperty("message").GetString(), "部署结果", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
