using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClashRemoteSsh.Core;

namespace ClashRemoteSsh.App.Controls;

public enum NoticeKind { Information, Success, Error }

/// <summary>Nonblocking notices and explicit confirmations rendered inside the existing window.</summary>
public static class InAppFeedback
{
    private sealed class Layer
    {
        public required Grid Root;
        public required UIElement Content;
        public required SmoothBorder Notice;
        public required TextBlock Message;
        public readonly DispatcherTimer Timer = new();
        public NoticeKind Kind;
        public TaskCompletionSource<bool>? Pending;
    }
    private static readonly ConditionalWeakTable<Window, Layer> Layers = new();
    // Attach before bindings populate selectors; reparenting a live ComboBox can clear its selection.
    public static void Attach(Window window) => For(window);
    private static Layer For(Window window)
    {
        window.Dispatcher.VerifyAccess();
        return Layers.GetValue(window, owner =>
        {
            var content = owner.Content as UIElement ?? throw new InvalidOperationException("窗口尚未准备好。");
            var root = new Grid();
            owner.Content = null; root.Children.Add(content); owner.Content = root;
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13, LineHeight = 21 };
            AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
            var notice = new SmoothBorder { Child = message, CornerRadius = new(16), Padding = new(18, 12, 18, 12),
                Margin = new(24, 20, 24, 92), MaxWidth = 560, MaxHeight = 200,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed, IsHitTestVisible = false, ClipToBounds = true };
            Panel.SetZIndex(notice, 50); root.Children.Add(notice);
            var layer = new Layer { Root = root, Content = content, Notice = notice, Message = message };
            layer.Timer.Tick += (_, _) => { layer.Timer.Stop(); notice.Visibility = Visibility.Collapsed; };
            owner.Closed += (_, _) => { layer.Timer.Stop(); layer.Pending?.TrySetResult(false); };
            return layer;
        });
    }

    public static void Show(Window window, string text, NoticeKind kind = NoticeKind.Information)
    {
        var layer = For(window);
        layer.Timer.Stop(); layer.Kind = kind;
        layer.Message.Text = Redactor.Clean(text);
        layer.Message.SetResourceReference(TextBlock.ForegroundProperty, kind == NoticeKind.Success ? "Success" : kind == NoticeKind.Error ? "Error" : "Ink");
        layer.Notice.SetResourceReference(Border.BackgroundProperty, kind == NoticeKind.Success ? "SuccessSurface" : kind == NoticeKind.Error ? "ErrorSurface" : "Block");
        layer.Notice.Visibility = Visibility.Visible;
        layer.Notice.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion.Fast));
        layer.Timer.Interval = TimeSpan.FromSeconds(kind == NoticeKind.Error ? 15 : 8); layer.Timer.Start();
    }
    public static void Dismiss(Window window)
    {
        if (!Layers.TryGetValue(window, out var layer)) return;
        layer.Timer.Stop(); layer.Notice.Visibility = Visibility.Collapsed;
    }
    internal static (string Text, NoticeKind Kind, bool Visible) Current(Window window)
        => Layers.TryGetValue(window, out var layer) ? (layer.Message.Text, layer.Kind, layer.Notice.IsVisible) : ("", NoticeKind.Information, false);

    public static Task<bool> ConfirmAsync(Window window, string message, string title = "确认操作")
    {
        var layer = For(window);
        if (layer.Pending != null) return layer.Pending.Task;
        var result = layer.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousFocus = Keyboard.FocusedElement;
        var wasEnabled = layer.Content.IsEnabled;
        layer.Content.SetCurrentValue(UIElement.IsEnabledProperty, false);
        var overlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)) };
        Panel.SetZIndex(overlay, 100);
        var card = new SmoothBorder { MaxWidth = 520, Margin = new(28), Padding = new(24), CornerRadius = new(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.SetResourceReference(Border.BackgroundProperty, "CardSurface");
        var content = new StackPanel(); card.Child = content;
        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 14) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        var body = new TextBlock { Text = Redactor.Clean(message), TextWrapping = TextWrapping.Wrap, LineHeight = 23, Margin = new(0, 0, 0, 22) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
        content.Children.Add(heading); content.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", MinWidth = 80, Margin = new(0, 0, 10, 0) };
        var accept = new Button { Content = "确认", MinWidth = 80, Style = (Style)window.FindResource("PrimaryButton") };
        buttons.Children.Add(cancel); buttons.Children.Add(accept); content.Children.Add(buttons);
        void Finish(bool accepted)
        {
            if (layer.Pending != result) return;
            layer.Pending = null; layer.Root.Children.Remove(overlay);
            layer.Content.SetCurrentValue(UIElement.IsEnabledProperty, wasEnabled);
            if (window.IsVisible && previousFocus != null) Keyboard.Focus(previousFocus);
            result.TrySetResult(accepted);
        }
        cancel.Click += (_, _) => Finish(false); accept.Click += (_, _) => Finish(true);
        overlay.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(false); } };
        overlay.Children.Add(card); layer.Root.Children.Add(overlay); cancel.Focus();
        return result.Task;
    }
}
