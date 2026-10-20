using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ClashOnWebSSH.Core;

namespace ClashOnWebSSH.App;

public partial class MainWindow
{
    private bool _pythonAvailable, _yamlAvailable;
    public ObservableCollection<ConfigurationProfile> Configurations { get; } = new();
    public bool CanReadProfiles => CanOperate && _pythonAvailable;
    public bool CanImportProfiles => CanReadProfiles && _yamlAvailable;
    public bool CanManageService => CanReadProfiles && _installed;
    public string ProfilesHint => !_connected ? "连接服务器后，可导入订阅链接和 YAML 文件。" : !_yamlAvailable
        ? "请在服务管理的环境维护中准备 Python / YAML，再导入配置。"
        : "订阅链接与 YAML 文件统一管理。导入不会改变当前代理；使用配置时会校验、备份并重启服务。";
    public Visibility NoProfilesVisibility => Configurations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InstalledVisibility => _connected && _installed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UninstalledVisibility => _connected && !_installed ? Visibility.Visible : Visibility.Collapsed;

    private async Task RefreshProfiles()
    {
        if (_remote == null || !_pythonAvailable) return;
        var result = await _remote.InvokeAsync("profiles.list");
        var list = JsonSerializer.Deserialize<List<ConfigurationProfile>>(result.Data.GetProperty("profiles").GetRawText(), _json) ?? new();
        Configurations.Clear(); foreach (var profile in list) Configurations.Add(profile); Notify();
    }
    private async void RefreshProfilesClicked(object sender, RoutedEventArgs e) => await Run("刷新配置列表", RefreshProfiles);
    private async void ImportProfileClicked(object sender, RoutedEventArgs e)
    {
        if (_demo) { await Run("导入配置", () => Task.CompletedTask); return; }
        var dialog = new ProfileImportWindow { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } input) return;
        await Run("导入配置（下载与校验）", async () =>
        {
            var payload = await ProfileTransfer.PrepareAsync(input);
            await _remote!.InvokeAsync("profiles.import", payload);
            await RefreshProfiles();
        });
    }
    private async void ApplyProfileClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ConfigurationProfile profile) return;
        if (!_installed) { await ShowDeploymentWizard(profile.Id); return; }
        if (!_demo && !Confirm($"使用「{profile.Name}」替换当前运行配置并重启服务？\n连接参数和回环监听会保留，失败将尝试恢复旧配置。", "切换配置")) return;
        await Run("切换配置", async () => { await _remote!.InvokeAsync("profiles.apply", new { profileId = profile.Id }); await RefreshState(); });
    }
    private async void UpdateProfileClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ConfigurationProfile profile) await UpdateConfiguration(profile);
    }
    private async Task UpdateConfiguration(ConfigurationProfile profile)
    {
        if (!_demo && profile.Active && !Confirm($"「{profile.Name}」正在使用，更新成功后会重启服务。继续？", "更新当前配置")) return;
        await Run("更新订阅配置", async () =>
        {
            var source = await _remote!.InvokeAsync("profiles.source", new { profileId = profile.Id });
            var download = await new SubscriptionDownloader().DownloadAsync(source.Data.GetProperty("url").GetString()!);
            await _remote.InvokeAsync("profiles.update", new { profileId = profile.Id, contentBase64 = download.ContentBase64, subscriptionUserinfo = download.SubscriptionUserinfo });
            await RefreshState();
        });
    }
    private void ProfileMenuClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ConfigurationProfile profile || !CanOperate) return;
        var menu = new ContextMenu();
        var refresh = new MenuItem { Header = profile.UpdateText, IsEnabled = profile.CanRefresh };
        refresh.Click += async (_, _) => await UpdateConfiguration(profile);
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += async (_, _) =>
        {
            var dialog = new RenameProfileWindow(profile.Name) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            await Run("重命名配置", async () => { await _remote!.InvokeAsync("profiles.rename", new { profileId = profile.Id, name = dialog.Value }); await RefreshProfiles(); });
        };
        var delete = new MenuItem { Header = "删除配置", IsEnabled = profile.CanDelete };
        delete.Click += async (_, _) =>
        {
            if (!Confirm("删除「" + profile.Name + "」及其远端保存的来源？", "删除配置")) return;
            await Run("删除配置", async () => { await _remote!.InvokeAsync("profiles.delete", new { profileId = profile.Id }); await RefreshProfiles(); });
        };
        menu.Items.Add(refresh); menu.Items.Add(rename); menu.Items.Add(new Separator()); menu.Items.Add(delete);
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;
    }
    private async Task ShowDeploymentWizard(string? profileId = null)
    {
        if (_demo || _remote == null || _busy) return;
        new DeploymentWindow(_remote, _pythonAvailable, preferredProfileId: profileId) { Owner = this }.ShowDialog();
        await Run("刷新远端状态", async () =>
        {
            if (!_pythonAvailable)
            {
                var capabilities = await _remote.CheckConnectionAsync();
                _pythonAvailable = capabilities.Contains("python3");
            }
            if (_pythonAvailable) await RefreshState();
        });
    }
    private async void OpenWizardClicked(object sender, RoutedEventArgs e) => await ShowDeploymentWizard();
    private async void UninstallClicked(object sender, RoutedEventArgs e)
    {
        JsonElement plan = default;
        await Run("读取卸载清单", async () => plan = (await _remote!.InvokeAsync("uninstall.plan")).Data);
        if (plan.ValueKind != JsonValueKind.Object || new UninstallWindow(plan) { Owner = this }.ShowDialog() != true) return;
        await Run("移除并卸载当前服务", async () =>
        {
            var result = await _remote!.InvokeAsync("uninstall", new { confirmService = plan.GetProperty("service").GetString(), confirmationToken = plan.GetProperty("confirmationToken").GetString() });
            await RefreshState();
            MessageBox.Show(this, Text(result.Data, "message"), "卸载结果", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }
}

internal sealed class RenameProfileWindow : Window
{
    private readonly TextBox _input;
    public string Value => _input.Text.Trim();
    public RenameProfileWindow(string name)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "重命名配置"; Width = 420; Height = 215; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "配置名称", Margin = new Thickness(0, 0, 0, 10) });
        _input = new TextBox { Text = name, MaxLength = 80 }; panel.Children.Add(_input);
        var save = new Button { Content = "保存", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        save.Click += (_, _) => { if (Value.Length > 0) DialogResult = true; }; panel.Children.Add(save); Content = panel;
    }
}
