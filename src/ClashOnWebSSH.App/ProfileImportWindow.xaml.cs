using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ClashOnWebSSH.App;

public sealed record ProfileImportData(string Name, string Kind, string Url, string ContentBase64, string FileName);
public partial class ProfileImportWindow : Window
{
    public ProfileImportData? Result { get; private set; }
    public ProfileImportWindow() { InitializeComponent(); Closed += (_, _) => UrlBox.Clear(); }
    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入 YAML 配置", Filter = "YAML 配置|*.yaml;*.yml", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
        {
            FileBox.Text = dialog.FileName;
            if (NameBox.Text == "新的订阅配置") NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }
    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        ImportButton.IsEnabled = false;
        try
        {
            var name = NameBox.Text.Trim();
            if (name.Length == 0 || name.Any(char.IsControl)) throw new ArgumentException("请填写有效的配置名称。");
            if (ImportTabs.SelectedIndex == 0)
            {
                var url = UrlBox.Password.Trim();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
                    throw new ArgumentException("请输入有效的 HTTPS 订阅地址。");
                Result = new(name, "url", url, "", "");
            }
            else
            {
                if (!File.Exists(FileBox.Text)) throw new ArgumentException("请先选择 YAML 文件。");
                if (new FileInfo(FileBox.Text).Length > 10 * 1024 * 1024) throw new ArgumentException("文件不能超过10MiB。");
                var bytes = await File.ReadAllBytesAsync(FileBox.Text);
                if (bytes.Length > 10 * 1024 * 1024) throw new ArgumentException("文件不能超过10MiB。");
                Result = new(name, "file", "", Convert.ToBase64String(bytes), Path.GetFileName(FileBox.Text));
            }
            DialogResult = true;
        }
        catch (Exception error) { ErrorText.Text = Core.Redactor.Clean(error.Message); }
        finally { ImportButton.IsEnabled = true; }
    }
}
