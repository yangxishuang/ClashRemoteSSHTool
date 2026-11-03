using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ClashRemoteSsh.App.Controls;

// Equal-width tiles, generated only for the viewport and one extra row on each side.
public sealed class VirtualizingCardPanel : VirtualizingPanel, IScrollInfo
{
    private const double Gap = 8, CardHeight = 112, RowHeight = CardHeight + Gap;
    private Size _extent, _viewport;
    private double _offset, _cardWidth;
    private int _columns = 1;
    public int Columns => _columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner == null) return new Size();
        var width = double.IsInfinity(availableSize.Width) ? 250 : Math.Max(0, availableSize.Width);
        var height = double.IsInfinity(availableSize.Height) ? RowHeight * 4 : Math.Max(0, availableSize.Height);
        // FlClash standard layout uses ceil(viewWidth / 250), with at least two columns.
        // A single-column fallback also supports hosts narrower than the desktop minimum.
        var columns = width < 400 ? 1 : Math.Max(2, (int)Math.Ceiling(width / 250));
        if (columns != _columns)
        {
            var firstVisible = (int)(_offset / RowHeight) * _columns;
            _offset = firstVisible / columns * RowHeight;
            _columns = columns;
        }
        _cardWidth = Math.Max(0, (width - Gap * (_columns - 1)) / _columns);
        _viewport = new Size(width, height);
        var count = owner.Items.Count;
        _extent = new Size(width, Math.Max(0, Math.Ceiling((double)count / _columns) * RowHeight - Gap));
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _extent.Height - height));
        ScrollOwner?.InvalidateScrollInfo();

        var first = Math.Max(0, ((int)(_offset / RowHeight) - 1) * _columns);
        var last = Math.Min(count - 1, ((int)Math.Ceiling((_offset + height) / RowHeight) + 1) * _columns - 1);
        // Accessing InternalChildren first lets WPF connect the items host's generator.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        for (var childIndex = children.Count - 1; childIndex >= 0; childIndex--)
        {
            var position = new GeneratorPosition(childIndex, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(childIndex, 1);
        }
        if (last >= first)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var isNew);
                    if (isNew)
                    {
                        if (childIndex == InternalChildren.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(new Size(_cardWidth, CardHeight));
                }
        }
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var childIndex = 0; childIndex < InternalChildren.Count; childIndex++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(childIndex, 0));
            InternalChildren[childIndex].Arrange(new Rect(index % _columns * (_cardWidth + Gap),
                index / _columns * RowHeight - _offset, _cardWidth, CardHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                _offset = 0;
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
        }
        InvalidateMeasure();
    }

    protected override void BringIndexIntoView(int index)
    {
        var count = ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;
        if (index < 0 || index >= count) return;
        var top = index / _columns * RowHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (top + CardHeight > _offset + _viewport.Height)
            SetVerticalOffset(top + CardHeight - _viewport.Height);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        DependencyObject? container = visual;
        while (container != null && VisualTreeHelper.GetParent(container) != this)
            container = VisualTreeHelper.GetParent(container);
        if (container is not UIElement element) return Rect.Empty;
        var index = ItemsControl.GetItemsOwner(this).ItemContainerGenerator.IndexFromContainer(element);
        BringIndexIntoView(index);
        return new Rect(index % _columns * (_cardWidth + Gap), index / _columns * RowHeight - _offset,
            _cardWidth, CardHeight);
    }

    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        var next = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(next - _offset) < 0.01) return;
        _offset = next;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }
    public void LineUp() => SetVerticalOffset(_offset - RowHeight / 3);
    public void LineDown() => SetVerticalOffset(_offset + RowHeight / 3);
    public void MouseWheelUp() => SetVerticalOffset(_offset - RowHeight);
    public void MouseWheelDown() => SetVerticalOffset(_offset + RowHeight);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double offset) { }
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
}
