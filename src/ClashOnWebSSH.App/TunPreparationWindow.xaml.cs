using System.ComponentModel;
using System.Windows;
using ClashOnWebSSH.Core;
using ClashOnWebSSH.App.Controls;

namespace ClashOnWebSSH.App;

public partial class TunPreparationWindow : Window, INotifyPropertyChanged
{
    private readonly RemoteActions _remote;
    private bool _busy = true, _offline, _completed;
    private RemotePrivilegeMode _privileges = RemotePrivilegeMode.Unknown;
    private string _progress = "正在检查管理员权限…";
    public bool IsIdle => !_busy;
    public bool IsBusy => _busy;
    public bool CanPrepare => !_busy && !_completed && _privileges != RemotePrivilegeMode.SudoUnavailable;
    public bool CanEnterPassword => CanPrepare && _privileges is RemotePrivilegeMode.PasswordRequired or RemotePrivilegeMode.Unknown;
    public Visibility PlaceholderVisibility => SudoBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OfflineVisibility => _offline ? Visibility.Visible : Visibility.Collapsed;
    public string ProgressText => _progress;
    public string PrivilegeText => _privileges switch
    {
        RemotePrivilegeMode.Root => "当前以 root 登录，无需输入管理员密码。",
        RemotePrivilegeMode.PasswordlessSudo => "已检测到免密 sudo，无需输入管理员密码。",
        RemotePrivilegeMode.SudoUnavailable => "服务器未安装 sudo，请联系管理员。",
        _ => "填写远端 Linux 用户的 sudo 密码，仅本次使用。"
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    public TunPreparationWindow(RemoteActions remote)
    {
        _remote = remote; InitializeComponent(); InAppFeedback.Attach(this); DataContext = this;
        Loaded += async (_, _) =>
        {
            try { _privileges = await _remote.CheckPrivilegesAsync(); _progress = "确认准备后将重启远端代理服务。"; }
            catch (Exception error) { _progress = Redactor.Clean(error.Message); }
            finally { _busy = false; Notify(); }
        };
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => SudoBox.Clear();
    }
    private void Notify() => PropertyChanged?.Invoke(this, new(null));
    private void PasswordChanged(object sender, RoutedEventArgs e) => Notify();
    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
    private async void PrepareClicked(object sender, RoutedEventArgs e) => await Prepare();
    private async void OfflineClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "选择对应架构的官方 mihomo v1.19.31 内核包", Filter = "Gzip 内核包|*.gz", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await Prepare(picker.FileName);
    }
    private async Task Prepare(string? offlinePackage = null)
    {
        if (!CanPrepare) return;
        var password = SudoBox.Password;
        SudoBox.Clear(); _busy = true; _offline = false; Notify();
        try
        {
            var progress = new Progress<DeploymentProgress>(value => { if (_busy) { _progress = value.Text; Notify(); } });
            var reply = await _remote.PrepareTunPermissionsAsync(password, progress, offlinePackage);
            _progress = reply.Data.GetProperty("message").GetString() ?? "权限已准备";
            _completed = true; InAppFeedback.Show(this, _progress, NoticeKind.Success);
        }
        catch (KernelAcquisitionException error) { _offline = true; _progress = Redactor.Clean(error.Message); InAppFeedback.Show(this, _progress, NoticeKind.Error); }
        catch (Exception error) { _progress = Redactor.Clean(error.Message); InAppFeedback.Show(this, _progress, NoticeKind.Error); }
        finally { password = ""; _busy = false; Notify(); }
    }
}
