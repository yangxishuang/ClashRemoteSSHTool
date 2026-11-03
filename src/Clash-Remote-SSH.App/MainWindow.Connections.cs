using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ClashRemoteSsh.App.Connections;
using ClashRemoteSsh.App.Controls;
using ClashRemoteSsh.Core;

namespace ClashRemoteSsh.App;

public partial class MainWindow
{
    private readonly SavedConnectionStore _savedConnections;
    private bool _fillingConnection = true;
    private SavedConnection? _selectedConnection;
    public ObservableCollection<SavedConnection> SavedConnections { get; } = [];
    public bool CanForgetConnection => CanEditConnection && _selectedConnection is not null;
    internal Func<ConnectionProfile, string, ISshTransport>? ConnectionTransportFactory { get; set; }

    private void InitializeConnections()
    {
        if (_demo)
        {
            SavedConnections.Add(new() { Profile = new() { Host = "linux.example.com", Username = "alice" } });
            SavedConnections.Add(new() { Profile = new() { Host = "backup.example.com", Username = "admin", Port = 2222, Authentication = AuthenticationMode.Password } });
            FillConnection(SavedConnections[0].Profile);
        }
        else
        {
            FillConnection(_settings.Load());
            try
            {
                ReplaceSavedConnections(_savedConnections.Load());
                if (SavedConnections.Count > 0) SavedServersBox.SelectedIndex = 0;
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
            { Loaded += (_, _) => InAppFeedback.Show(this, error.Message, NoticeKind.Error); }
        }
        _fillingConnection = false;
    }

    private void FillConnection(ConnectionProfile profile)
    {
        _fillingConnection = true;
        try
        {
            LoginPassword.Clear();
            HostBox.Text = profile.Host; UserBox.Text = profile.Username; PortBox.Text = profile.Port.ToString();
            IdentityBox.Text = profile.IdentityFile; ServiceBox.Text = profile.ServiceName; ConfigBox.Text = profile.ConfigPath;
            ApiPortBox.Text = profile.ControllerPort.ToString(); AuthBox.SelectedIndex = (int)profile.Authentication;
        }
        finally { _fillingConnection = false; }
    }

    private void ReplaceSavedConnections(IReadOnlyList<SavedConnection> entries)
    {
        _fillingConnection = true;
        try
        {
            SavedServersBox.SelectedIndex = -1;
            SavedConnections.Clear();
            foreach (var entry in entries) SavedConnections.Add(entry);
        }
        finally { _fillingConnection = false; }
    }

    private void SavedServerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingConnection || !CanEditConnection || SavedServersBox.SelectedItem is not SavedConnection saved) return;
        FillConnection(saved.Profile);
        _selectedConnection = saved;
        RestoreSelectedPassword();
        Notify();
    }

    private void RestoreSelectedPassword()
    {
        try { if (!_demo && _selectedConnection is not null) LoginPassword.Password = _savedConnections.ReadPassword(_selectedConnection); }
        catch (InvalidOperationException error) { InAppFeedback.Show(this, error.Message, NoticeKind.Error); }
    }

    private void SavedServerDropdownClosed(object? sender, EventArgs e)
    {
        // Choosing the already selected item does not raise SelectionChanged; allow it to refill after a failed attempt.
        if (CanEditConnection && LoginPassword.Password.Length == 0) RestoreSelectedPassword();
    }

    private void ConnectionTargetEdited(object sender, TextChangedEventArgs e) => ClearSelectedConnection();
    private void ClearSelectedConnection()
    {
        if (_fillingConnection || LoginPassword is null) return;
        // Never send an automatically filled password to a newly typed host/user/port/key/authentication mode.
        if (_selectedConnection is not null) LoginPassword.Clear();
        _selectedConnection = null;
        SavedServersBox.SelectedIndex = -1;
        Notify();
    }

    private void ForgetConnectionClicked(object sender, RoutedEventArgs e)
    {
        if (!CanForgetConnection || _demo) return;
        try
        {
            ReplaceSavedConnections(_savedConnections.Forget(_selectedConnection!.Profile));
            _selectedConnection = null; LoginPassword.Clear(); Notify();
            InAppFeedback.Show(this, "已删除连接记录及保存的密码。", NoticeKind.Success);
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { InAppFeedback.Show(this, error.Message, NoticeKind.Error); }
    }

    private string RememberConnection(ConnectionProfile profile, string password)
    {
        try
        {
            ReplaceSavedConnections(_savedConnections.Remember(profile, password));
            _selectedConnection = SavedConnections[0];
            _fillingConnection = true;
            try { SavedServersBox.SelectedIndex = 0; }
            finally { _fillingConnection = false; }
            _settings.Save(profile);
            return "连接服务器完成，已保存连接信息";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { return "SSH 已连接，但连接信息未能保存，请检查本机设置目录。"; }
    }
}
