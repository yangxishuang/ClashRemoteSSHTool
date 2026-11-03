using System.Windows;
using System.Windows.Controls;

namespace ClashRemoteSsh.App;

public partial class MainWindow
{
    private bool _syncingProxyMode;
    private int _previousProxyMode;
    private string _modeOperation = "";
    private static readonly string[] ProxyModes = ["rule", "global", "direct"];
    public bool CanChangeProxyMode => CanReadNetwork && _installed && _networkKnown && _networkApiAvailable && _tunRunning && !_tunPending && ProxyModes.Contains(_takeoverMode);
    public bool IsModeChanging => _busy && _modeOperation.Length > 0;
    public string ProxyModeStatus => _modeOperation.Length > 0 ? _modeOperation
        : !_connected ? "连接服务器后选择代理模式。"
        : !_networkKnown ? "模式状态未确认，请刷新网络状态。"
        : !_networkApiAvailable || !_tunRunning ? "请先启动代理服务并恢复管理接口。"
        : _tunPending ? "等待 TUN 连通确认或自动回退。"
        : _bothOff ? $"当前：{ModeText} · 两个代理入口均关闭"
        : $"当前：{ModeText} · 切换时重连已有代理连接";

    private void SynchronizeProxyMode()
    {
        if (ProxyModeTabs is null) return;
        _syncingProxyMode = true;
        try { ProxyModeTabs.SelectedIndex = _connected && _networkKnown && _networkApiAvailable ? Array.IndexOf(ProxyModes, _takeoverMode) : -1; }
        finally { _syncingProxyMode = false; }
    }
    private async void ProxyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs || !ReferenceEquals(e.Source, tabs) || tabs.SelectedIndex < 0) return;
        SlideTabContent(tabs, _previousProxyMode);
        _previousProxyMode = tabs.SelectedIndex;
        if (_syncingProxyMode) return;
        var requested = ProxyModes[tabs.SelectedIndex];
        if (!CanChangeProxyMode || requested == _takeoverMode) { SynchronizeProxyMode(); return; }
        _modeOperation = "正在切换并核对服务器模式…";
        try
        {
            await Run("切换代理模式", async () =>
            {
                try { await _remote!.InvokeAsync("network.mode", new { mode = requested }); }
                finally { await RecheckNetwork(true); }
                if (!_networkKnown || _takeoverMode != requested)
                    throw new InvalidOperationException("最新代理模式未能确认，请刷新后核对，不要重复提交。");
            }, () => "代理模式已切换并保存");
        }
        finally { _modeOperation = ""; SynchronizeProxyMode(); Notify(); }
    }
}
