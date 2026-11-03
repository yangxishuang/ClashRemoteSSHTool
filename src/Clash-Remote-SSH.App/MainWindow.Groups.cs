using System.Text.Json;

namespace ClashRemoteSsh.App;

public partial class MainWindow
{
    private string _groupConfigurationHash = "";
    private string _groupMode = "";
    internal void ApplyGroups(JsonElement data)
    {
        var hash = Text(data, "configurationHash");
        var mode = Text(data, "mode");
        var previous = hash == _groupConfigurationHash && mode == _groupMode ? _group?.Name : null;
        var preferred = Text(data, "defaultGroup");
        var list = JsonSerializer.Deserialize<List<ProxyGroup>>(data.GetProperty("groups").GetRawText(), _json) ?? new();
        Groups.Clear(); foreach (var group in list) Groups.Add(group);
        GroupBox.SelectedItem = Groups.FirstOrDefault(g => g.Name == previous)
            ?? Groups.FirstOrDefault(g => g.Name == preferred) ?? Groups.FirstOrDefault();
        _groupConfigurationHash = hash;
        _groupMode = mode;
        Notify();
    }
}
