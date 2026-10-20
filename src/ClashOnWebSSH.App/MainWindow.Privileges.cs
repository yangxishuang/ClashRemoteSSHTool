using System.Windows;
using ClashOnWebSSH.Core;

namespace ClashOnWebSSH.App;

public partial class MainWindow
{
    private RemotePrivilegeMode _maintenancePrivileges = RemotePrivilegeMode.Unknown;
    private bool _maintenancePrivilegesChecked;
    public bool CanEnterMaintenancePassword => CanOperate && _maintenancePrivilegesChecked && _maintenancePrivileges is RemotePrivilegeMode.PasswordRequired or RemotePrivilegeMode.Unknown;
    public Visibility MaintenancePlaceholderVisibility => SudoPassword.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string MaintenancePrivilegeText => !_maintenancePrivilegesChecked ? "连接后自动检查管理员权限。" : _maintenancePrivileges switch
    {
        RemotePrivilegeMode.Root => "当前以 root 登录，无需输入管理员密码。",
        RemotePrivilegeMode.PasswordlessSudo => "已检测到免密 sudo，无需输入管理员密码。",
        RemotePrivilegeMode.SudoUnavailable => "服务器未安装 sudo，请由管理员准备环境。",
        _ => "填写远端 Linux 用户的 sudo 密码，仅本次使用，不会保存。"
    };
    private void MaintenancePasswordChanged(object sender, RoutedEventArgs e) => Notify();
    private async Task RefreshMaintenancePrivilegesAsync()
    {
        if (_remote is null) return;
        try { _maintenancePrivileges = await _remote.CheckPrivilegesAsync(); }
        catch (Exception) { _maintenancePrivileges = RemotePrivilegeMode.Unknown; }
        _maintenancePrivilegesChecked = true;
        if (_maintenancePrivileges is RemotePrivilegeMode.Root or RemotePrivilegeMode.PasswordlessSudo or RemotePrivilegeMode.SudoUnavailable) SudoPassword.Clear();
        Notify();
    }
}
