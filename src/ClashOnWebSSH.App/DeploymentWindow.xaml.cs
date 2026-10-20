using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ClashOnWebSSH.Core;

namespace ClashOnWebSSH.App;

public partial class DeploymentWindow : Window, INotifyPropertyChanged
{
    private enum OperationStep { None, Environment, Configuration, Deployment }
    private readonly RemoteActions _remote;
    private readonly bool _demo;
    private readonly string? _preferredProfileId;
    private readonly DispatcherTimer _operationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _elapsed = new();
    private bool _busy, _python, _yaml, _systemd, _installed, _linger, _privilegesChecked;
    private RemotePrivilegeMode _privileges = RemotePrivilegeMode.Unknown;
    private OperationStep _activeStep;
    private string _status = "先检查依赖，再导入一个配置。", _operationTitle = "";
    public ObservableCollection<ConfigurationProfile> Profiles { get; } = new();
    public bool IsIdle => !_busy;
    public bool CanImport => !_busy && _python && _yaml;
    public bool CanDeploy => CanImport && _systemd && !_installed;
    public bool CanEnterSudoPassword => IsIdle && _privilegesChecked && _privileges is RemotePrivilegeMode.PasswordRequired or RemotePrivilegeMode.Unknown;
    public Visibility SudoPlaceholderVisibility => SudoBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsEnvironmentBusy => _busy && _activeStep == OperationStep.Environment;
    public bool IsConfigurationBusy => _busy && _activeStep == OperationStep.Configuration;
    public bool IsDeploymentBusy => _busy && _activeStep == OperationStep.Deployment;
    public string OperationText => $"{_operationTitle} · 已用时 {(int)_elapsed.Elapsed.TotalMinutes:00}:{_elapsed.Elapsed.Seconds:00}";
    public string StatusText => _status;
    public string PrivilegeText => !_privilegesChecked ? "正在检查管理员权限…" : _privileges switch
    {
        RemotePrivilegeMode.Root => "当前以 root 登录，无需输入管理员密码。",
        RemotePrivilegeMode.PasswordlessSudo => "已检测到免密 sudo，无需输入管理员密码。",
        RemotePrivilegeMode.PasswordRequired => "填写远端 Linux 用户的 sudo 密码，仅用于本次操作，不会保存。",
        RemotePrivilegeMode.SudoUnavailable => "服务器未安装 sudo；请先由管理员准备依赖和后台运行权限。",
        _ => "暂未确认提权方式，需要时可填写远端用户的管理员密码。"
    };
    public string EnvironmentText => $"Python {(_python ? "就绪" : "缺失")} · YAML {(_yaml ? "就绪" : "缺失")} · 用户会话 {(_systemd ? "可用" : "待检查")} · linger {(_linger ? "已开启" : "未开启")}。";
    public event PropertyChangedEventHandler? PropertyChanged;
    public DeploymentWindow(RemoteActions? remote, bool pythonAvailable, bool demo = false, string? preferredProfileId = null)
    {
        _remote = remote!; _python = pythonAvailable; _demo = demo; _preferredProfileId = preferredProfileId;
        InitializeComponent(); DataContext = this;
        _operationTimer.Tick += (_, _) => PropertyChanged?.Invoke(this, new(nameof(OperationText)));
        Loaded += async (_, _) =>
        {
            if (_demo)
            {
                _python = true; _yaml = true; _systemd = true;
                _privilegesChecked = true; _privileges = RemotePrivilegeMode.PasswordRequired;
                Profiles.Add(new() { Id = "demo", Name = "IPLC 国际流量（演示）", Kind = "url" }); ProfileBox.SelectedIndex = 0;
                _status = "向导界面预览，不连接或修改服务器。"; Notify();
            }
            else await RunTask(OperationStep.Environment, "读取部署环境", async () =>
            {
                await RefreshPrivilegesAsync();
                if (_python) await RefreshAsync();
            });
        };
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => { _operationTimer.Stop(); _elapsed.Stop(); SudoBox.Clear(); };
    }
    private void Notify() => PropertyChanged?.Invoke(this, new(null));
    private void SudoPasswordChanged(object sender, RoutedEventArgs e) => PropertyChanged?.Invoke(this, new(nameof(SudoPlaceholderVisibility)));
    private async Task RefreshPrivilegesAsync()
    {
        try { _privileges = await _remote.CheckPrivilegesAsync(); }
        catch (Exception) { _privileges = RemotePrivilegeMode.Unknown; }
        _privilegesChecked = true;
        if (_privileges is RemotePrivilegeMode.Root or RemotePrivilegeMode.PasswordlessSudo or RemotePrivilegeMode.SudoUnavailable) SudoBox.Clear();
        Notify();
    }
    private void BeginOperation(OperationStep step, string title)
    {
        _busy = true; _activeStep = step; _operationTitle = title; _status = title + "…";
        _elapsed.Restart(); _operationTimer.Start(); Notify();
    }
    private void EndOperation()
    {
        _operationTimer.Stop(); _elapsed.Stop(); _busy = false; _activeStep = OperationStep.None; Notify();
    }
    private async Task<bool> RunTask(OperationStep step, string title, Func<Task> operation)
    {
        if (_busy) return false;
        if (_demo) { MessageBox.Show(this, "当前为向导演示，不执行服务器操作。", "界面预览"); return false; }
        BeginOperation(step, title);
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            await operation(); _status = title + "完成"; return true;
        }
        catch (Exception error) { _status = Redactor.Clean(error.Message); return false; }
        finally { EndOperation(); }
    }
    // Only the explicit demo renderer uses these previews; they never execute SSH.
    internal void PreviewOperation(int step, RemotePrivilegeMode privileges)
    {
        if (!_demo) throw new InvalidOperationException("仅演示模式可预览操作状态。");
        _privileges = privileges; _privilegesChecked = true;
        EndOperation();
        if (step is >= 1 and <= 3)
            BeginOperation((OperationStep)step, step switch { 1 => "准备系统依赖", 2 => "下载并导入配置", _ => "部署并启动 Clash，下载可能需要几分钟" });
        Notify();
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
    private async void PrepareClicked(object sender, RoutedEventArgs e) => await RunTask(OperationStep.Environment, "准备系统依赖", async () =>
    {
        try { await _remote.BootstrapAsync(SudoBox.Password, false); _python = true; await RefreshPrivilegesAsync(); await RefreshAsync(); }
        finally { SudoBox.Clear(); }
    });
    private async void LingerClicked(object sender, RoutedEventArgs e) => await RunTask(OperationStep.Environment, "启用脱离登录运行", async () =>
    {
        try { await _remote.BootstrapAsync(SudoBox.Password, true); await RefreshPrivilegesAsync(); if (_python) await RefreshAsync(); }
        finally { SudoBox.Clear(); }
    });
    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ProfileImportWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } input) return;
        await RunTask(OperationStep.Configuration, "导入配置", async () =>
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
        string? message = null;
        if (await RunTask(OperationStep.Deployment, "部署并启动 Clash，下载可能需要几分钟", async () =>
        {
            var result = await _remote.InvokeAsync("deploy", new { profileId = profile.Id });
            await RefreshAsync(); message = result.Data.GetProperty("message").GetString();
        })) MessageBox.Show(this, message, "部署结果", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
