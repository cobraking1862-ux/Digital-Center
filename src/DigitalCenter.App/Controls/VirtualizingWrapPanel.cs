using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace DigitalCenter.App.Controls;

/// <summary>
/// Pixel-scrolling virtualizing wrap panel.
///
/// The poster grids can hold thousands of items; a plain WrapPanel would
/// materialize every card. This panel realizes only the rows that intersect
/// the viewport (realization-only virtualization — no container recycling),
/// which bounds the number of live containers to roughly one screen of cards
/// while keeping the implementation small and predictable.
///
/// Layout: fixed cells of <see cref="ItemWidth"/> × <see cref="ItemHeight"/>
/// flowing left-to-right, wrapping into as many columns as the viewport
/// width allows, plus <see cref="SpacingX"/>/<see cref="SpacingY"/> gutters.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingXProperty = DependencyProperty.Register(
        nameof(SpacingX), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingYProperty = DependencyProperty.Register(
        nameof(SpacingY), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const double LineDelta = 16.0;

    private readonly List<int> _realized = new(); // item index per internal child, ascending
    private Size _viewport = new(0, 0);
    private Size _extent = new(0, 0);
    private Point _offset = new(0, 0);
    private double _cellWidth;
    private double _cellHeight;
    private ArrangePlan _arrangePlan;
    private bool _canHorizontallyScroll;
    private bool _canVerticallyScroll;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public double SpacingX
    {
        get => (double)GetValue(SpacingXProperty);
        set => SetValue(SpacingXProperty, value);
    }

    public double SpacingY
    {
        get => (double)GetValue(SpacingYProperty);
        set => SetValue(SpacingYProperty, value);
    }

    public ScrollViewer? ScrollOwner { get; set; }

    public bool CanHorizontallyScroll
    {
        get => _canHorizontallyScroll;
        set => _canHorizontallyScroll = value;
    }

    public bool CanVerticallyScroll
    {
        get => _canVerticallyScroll;
        set => _canVerticallyScroll = value;
    }

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    public void LineUp() => SetVerticalOffset(VerticalOffset - LineDelta);

    public void LineDown() => SetVerticalOffset(VerticalOffset + LineDelta);

    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - LineDelta);

    public void LineRight() => SetHorizontalOffset(HorizontalOffset + LineDelta);

    public void PageUp() => SetVerticalOffset(VerticalOffset - _viewport.Height);

    public void PageDown() => SetVerticalOffset(VerticalOffset + _viewport.Height);

    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - _viewport.Width);

    public void PageRight() => SetHorizontalOffset(HorizontalOffset + _viewport.Width);

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - Math.Max(_cellHeight + SpacingY, LineDelta * 3));

    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + Math.Max(_cellHeight + SpacingY, LineDelta * 3));

    public void MouseWheelLeft() => SetHorizontalOffset(HorizontalOffset - LineDelta * 3);

    public void MouseWheelRight() => SetHorizontalOffset(HorizontalOffset + LineDelta * 3);

    public void SetHorizontalOffset(double offset)
    {
        var clamped = Clamp(offset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        if (Math.Abs(clamped - _offset.X) > 0.5)
        {
            _offset.X = clamped;
            InvalidateMeasure();
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    public void SetVerticalOffset(double offset)
    {
        var clamped = Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (Math.Abs(clamped - _offset.Y) > 0.5)
        {
            _offset.Y = clamped;
            InvalidateMeasure();
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        // A collection change (Clear/Add/Reset) can arrive while the panel is
        // detached or the generator is mid-reset. Dropping the realized book-
        // keeping must never throw into the caller (a VM refresh would catch
        // it as a load failure and leave the grid empty until the next
        // manual refresh) — so this is deliberately defensive.
        try
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                _realized.Clear();
                if (InternalChildren.Count > 0)
                {
                    RemoveInternalChildRange(0, InternalChildren.Count);
                }
            }
            else
            {
                RemoveAllRealized();
            }
        }
        catch (InvalidOperationException)
        {
            _realized.Clear();
        }

        InvalidateMeasure();
    }

    protected override void OnClearChildren()
    {
        base.OnClearChildren();
        _realized.Clear();
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // CRITICAL WPF REQUIREMENT:
        // In WPF's VirtualizingPanel/Panel implementation, ItemContainerGenerator is null
        // until InternalChildren is accessed for the first time (which invokes
        // Panel.VerifyBoundState() -> EnsureGenerator() -> ConnectToGenerator()).
        // Without touching InternalChildren first, ItemContainerGenerator evaluates to null
        // on cold startup when Items is already populated, causing MeasureOverride to bail
        // out with Size(0, 0) and never subscribe OnItemsChanged.
        var children = InternalChildren;

        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return new Size(0, 0);
        }

        var itemsOwner = ItemsControl.GetItemsOwner(this);
        var itemCount = itemsOwner?.Items.Count
                        ?? (generator as ItemContainerGenerator)?.Items.Count
                        ?? 0;
        if (itemCount == 0)
        {
            RemoveAllRealized();
            UpdateScrollInfo(availableSize, new Size(0, 0));
            return new Size(0, 0);
        }

        var ancestorScroller = FindParentScrollViewer(this);
        if (ancestorScroller is not null)
        {
            EnsureHookedToAncestorScrollViewer(ancestorScroller);
        }

        // ---- cell size: fixed, or probed from a realized child ---------------
        _cellWidth = double.IsNaN(ItemWidth) ? ProbeItemWidth() : ItemWidth;
        _cellHeight = double.IsNaN(ItemHeight) ? ProbeItemHeight() : ItemHeight;
        _cellWidth = Sanitize(_cellWidth, 200);
        _cellHeight = Sanitize(_cellHeight, 300);

        var stepX = _cellWidth + SpacingX;
        var stepY = _cellHeight + SpacingY;

        // ---- columns: fit the viewport (or fallback to ancestor ScrollViewer width)
        var viewportWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? (ancestorScroller is { ViewportWidth: > 0 } ? ancestorScroller.ViewportWidth
                : ancestorScroller is { ActualWidth: > 0 } ? ancestorScroller.ActualWidth : 0)
            : availableSize.Width;

        var columns = viewportWidth > 0
            ? Math.Max(1, (int)Math.Floor((viewportWidth + SpacingX) / stepX))
            : Math.Max(1, (int)Math.Floor((1080 + SpacingX) / stepX));

        var rows = (int)Math.Ceiling(itemCount / (double)columns);
        var extent = new Size(
            Math.Max(0, columns * stepX - SpacingX),
            Math.Max(0, rows * stepY - SpacingY));
        UpdateScrollInfo(availableSize, extent);

        // ---- visible window of items -----------------------------------------
        // If measured inside a plain ScrollViewer (CanContentScroll=False), availableSize.Height
        // is PositiveInfinity. Resolve the enclosing ScrollViewer viewport height and vertical offset
        // so we still only realize the visible rows plus a 2-row buffer instead of all items.
        var effectiveOffsetY = _offset.Y;
        var viewportHeight = availableSize.Height;
        if (double.IsInfinity(viewportHeight) || viewportHeight <= 0)
        {
            if (ancestorScroller is not null)
            {
                if (ancestorScroller.ViewportHeight > 0)
                {
                    viewportHeight = ancestorScroller.ViewportHeight;
                    effectiveOffsetY = ancestorScroller.VerticalOffset;
                    _lastMeasuredScrollOffset = effectiveOffsetY;
                    _lastMeasuredViewportHeight = viewportHeight;
                    _lastMeasuredViewportWidth = ancestorScroller.ViewportWidth;
                }
                else if (ancestorScroller.ActualHeight > 0)
                {
                    viewportHeight = ancestorScroller.ActualHeight;
                    effectiveOffsetY = ancestorScroller.VerticalOffset;
                }
                else
                {
                    viewportHeight = 800;
                }
            }
            else
            {
                viewportHeight = 800;
            }
        }

        var firstRow = Math.Max(0, (int)Math.Floor(effectiveOffsetY / stepY));
        var visibleRows = (int)Math.Ceiling(viewportHeight / stepY) + 2;
        var startIndex = Math.Min(itemCount - 1, firstRow * columns);
        var endIndex = Math.Min(itemCount - 1, (firstRow + visibleRows) * columns - 1);

        RealizeRange(generator, children, startIndex, endIndex);
        _arrangePlan = new ArrangePlan(columns, stepX, stepY, startIndex, double.IsInfinity(availableSize.Height));

        var desiredWidth = viewportWidth <= 0 ? extent.Width : Math.Min(viewportWidth, extent.Width);
        // When CanContentScroll=True (IScrollInfo mode), DesiredSize.Height must be bounded by
        // the smaller of extent.Height and availableSize.Height so we never force a parent
        // Auto-sized container to expand to infinity or trigger layout oscillation.
        var desiredHeight = double.IsInfinity(availableSize.Height)
            ? extent.Height
            : Math.Min(availableSize.Height, extent.Height);

        return new Size(
            double.IsFinite(desiredWidth) && desiredWidth > 0 ? desiredWidth : 0,
            double.IsFinite(desiredHeight) && desiredHeight > 0 ? desiredHeight : 0);
    }

    private ScrollViewer? _hookedScroller;

    private static ScrollViewer? FindParentScrollViewer(DependencyObject start)
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is ScrollViewer sv)
            {
                return sv;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void EnsureHookedToAncestorScrollViewer(ScrollViewer sv)
    {
        if (ReferenceEquals(_hookedScroller, sv))
        {
            return;
        }
        if (_hookedScroller is not null)
        {
            _hookedScroller.ScrollChanged -= OnAncestorScrollChanged;
            _hookedScroller.SizeChanged -= OnAncestorSizeChanged;
        }
        _hookedScroller = sv;
        _hookedScroller.ScrollChanged += OnAncestorScrollChanged;
        _hookedScroller.SizeChanged += OnAncestorSizeChanged;
    }

    private double _lastMeasuredScrollOffset = -1;
    private double _lastMeasuredViewportHeight = -1;
    private double _lastMeasuredViewportWidth = -1;

    private void OnAncestorSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged || e.HeightChanged)
        {
            InvalidateMeasure();
        }
    }

    private void OnAncestorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv)
        {
            return;
        }

        // Only invalidate measure when the user actually scrolls to a new vertical offset
        // or resizes the viewport window — never on ExtentHeightChange, which would cause
        // an infinite Measure <-> ScrollChanged layout loop that freezes the WPF UI thread.
        var offsetDiff = Math.Abs(sv.VerticalOffset - _lastMeasuredScrollOffset);
        var vpHeightDiff = Math.Abs(sv.ViewportHeight - _lastMeasuredViewportHeight);
        var vpWidthDiff = Math.Abs(sv.ViewportWidth - _lastMeasuredViewportWidth);

        if (offsetDiff > 8.0 || vpHeightDiff > 2.0 || vpWidthDiff > 2.0)
        {
            _lastMeasuredScrollOffset = sv.VerticalOffset;
            _lastMeasuredViewportHeight = sv.ViewportHeight;
            _lastMeasuredViewportWidth = sv.ViewportWidth;
            InvalidateMeasure();
        }
    }

    private readonly struct ArrangePlan(int columns, double stepX, double stepY, int startIndex, bool useLogicalOffset)
    {
        public int Columns { get; } = columns;
        public double StepX { get; } = stepX;
        public double StepY { get; } = stepY;
        public int StartIndex { get; } = startIndex;
        public bool UseLogicalOffset { get; } = useLogicalOffset;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < _realized.Count && i < InternalChildren.Count; i++)
        {
            var itemIndex = _realized[i];
            var relative = itemIndex - _arrangePlan.StartIndex;
            if (relative < 0)
            {
                continue;
            }

            var row = relative / _arrangePlan.Columns;
            var column = relative % _arrangePlan.Columns;
            var x = column * _arrangePlan.StepX - _offset.X;
            var y = _arrangePlan.UseLogicalOffset
                ? (itemIndex / _arrangePlan.Columns) * _arrangePlan.StepY
                : row * _arrangePlan.StepY - _offset.Y;

            InternalChildren[i].Arrange(new Rect(x, y, _cellWidth, _cellHeight));
        }

        return finalSize;
    }

    /// <summary>Derives the default cell width from an already-realized child.</summary>
    private double ProbeItemWidth()
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (child.DesiredSize.Width > 0)
            {
                return child.DesiredSize.Width;
            }
        }

        return 200;
    }

    /// <summary>Derives the default cell height from an already-realized child.</summary>
    private double ProbeItemHeight()
    {
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (child.DesiredSize.Height > 0)
            {
                return child.DesiredSize.Height;
            }
        }

        return 300;
    }

    private void RealizeRange(IItemContainerGenerator generator, UIElementCollection children, int startIndex, int endIndex)
    {
        if (_realized.Count != children.Count)
        {
            RemoveAllRealized();
        }

        // Drop everything outside the window first (from the end so indexes stay valid).
        for (var i = _realized.Count - 1; i >= 0; i--)
        {
            var index = _realized[i];
            if (index < startIndex || index > endIndex)
            {
                try
                {
                    generator.Remove(new GeneratorPosition(i, 0), 1);
                }
                catch (InvalidOperationException)
                {
                }

                RemoveInternalChildRange(i, 1);
                _realized.RemoveAt(i);
            }
        }

        var cellConstraint = new Size(_cellWidth, _cellHeight);

        for (var index = startIndex; index <= endIndex; index++)
        {
            var existingOrdinal = _realized.IndexOf(index);
            if (existingOrdinal >= 0 && existingOrdinal < children.Count)
            {
                // Always measure already-realized containers so layout invalidations
                // (e.g. async poster image loads or binding updates) propagate properly.
                children[existingOrdinal].Measure(cellConstraint);
                continue;
            }

            var position = generator.GeneratorPositionFromIndex(index);
            UIElement? child = null;

            using (generator.StartAt(position, GeneratorDirection.Forward, true))
            {
                var element = generator.GenerateNext(out var newlyRealized);
                if (element is UIElement uiElement)
                {
                    child = uiElement;
                    if (newlyRealized)
                    {
                        // Insert so _realized stays ascending alongside the child list.
                        var ordinal = CountRealizedBefore(index);
                        InsertInternalChild(ordinal, uiElement);
                        generator.PrepareItemContainer(uiElement);
                        _realized.Insert(ordinal, index);
                    }
                    else
                    {
                        var childIdx = children.IndexOf(uiElement);
                        if (childIdx < 0)
                        {
                            var ordinal = CountRealizedBefore(index);
                            InsertInternalChild(ordinal, uiElement);
                            generator.PrepareItemContainer(uiElement);
                            _realized.Insert(ordinal, index);
                        }
                        else if (!_realized.Contains(index))
                        {
                            _realized.Insert(CountRealizedBefore(index), index);
                        }
                    }
                }
            }

            child?.Measure(cellConstraint);
        }
    }

    private int CountRealizedBefore(int itemIndex)
    {
        var count = 0;
        foreach (var realizedIndex in _realized)
        {
            if (realizedIndex < itemIndex)
            {
                count++;
            }
            else
            {
                break;
            }
        }

        return count;
    }

    private void RemoveAllRealized()
    {
        var generator = ItemContainerGenerator;
        if (generator is not null)
        {
            for (var i = _realized.Count - 1; i >= 0; i--)
            {
                try
                {
                    generator.Remove(new GeneratorPosition(i, 0), 1);
                }
                catch (InvalidOperationException)
                {
                    // The generator was reset concurrently (collection change,
                    // template re-application); its realized set is already
                    // gone, so there is nothing left to remove.
                    break;
                }
            }
        }

        if (InternalChildren.Count > 0)
        {
            RemoveInternalChildRange(0, InternalChildren.Count);
        }

        _realized.Clear();
    }

    private void UpdateScrollInfo(Size availableSize, Size extent)
    {
        var viewport = new Size(
            double.IsInfinity(availableSize.Width) ? extent.Width : Math.Max(0, availableSize.Width),
            double.IsInfinity(availableSize.Height) ? extent.Height : Math.Max(0, availableSize.Height));

        var changed = Math.Abs(viewport.Width - _viewport.Width) > 0.5 ||
                      Math.Abs(viewport.Height - _viewport.Height) > 0.5 ||
                      Math.Abs(extent.Width - _extent.Width) > 0.5 ||
                      Math.Abs(extent.Height - _extent.Height) > 0.5;

        _viewport = viewport;
        _extent = extent;

        _offset.X = Clamp(_offset.X, 0, Math.Max(0, extent.Width - viewport.Width));
        _offset.Y = Clamp(_offset.Y, 0, Math.Max(0, extent.Height - viewport.Height));

        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private static double Sanitize(double value, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : value;

    private static double Clamp(double value, double min, double max) =>
        Math.Max(min, Math.Min(value, max));
}