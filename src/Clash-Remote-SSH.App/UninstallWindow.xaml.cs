using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ClashRemoteSsh.App;

public partial class UninstallWindow : Window
{
    private readonly string _service;
    public UninstallWindow(JsonElement plan)
    {
        _service = plan.GetProperty("service").GetString()!;
        InitializeComponent();
        var text = new StringBuilder("将停止、禁用并删除以下当前用户文件：\n\n");
        foreach (var file in plan.GetProperty("files").EnumerateArray())
            text.AppendLine(file.GetProperty("label").GetString()).AppendLine(file.GetProperty("path").GetString()).AppendLine();
        var binary = plan.GetProperty("retainedBinary").GetString();
        if (!string.IsNullOrEmpty(binary)) text.AppendLine("系统共享内核保留（不执行全局软件包卸载）：").AppendLine(binary).AppendLine();
        text.Append("已导入的链接、YAML 和当前配置快照保留，可用于重新部署。");
        PlanText.Text = text.ToString();
        ConfirmHint.Text = "请输入服务名确认：" + _service;
    }
    private void ConfirmationChanged(object sender, TextChangedEventArgs e) { if (RemoveButton != null) RemoveButton.IsEnabled = ConfirmBox.Text.Trim() == _service; }
    private void RemoveClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
