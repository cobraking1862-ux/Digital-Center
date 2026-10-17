using System;
using System.Collections.Generic;
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
        RemoveAllRealized();
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
        var generator = ItemContainerGenerator as ItemContainerGenerator;
        if (generator is null)
        {
            // Transitional state: the panel is not currently registered as the
            // items host of an ItemsControl (pre-attach measurement, template
            // re-application, or detach), so no containers can be realized.
            //
            // Never echo `availableSize` back here. WPF measures panels with
            // unbounded constraints in exactly these states, and returning
            // PositiveInfinity as DesiredSize violates the layout contract —
            // UIElement.Measure then throws on every layout pass
            // ("Layout measurement override ... should not return
            // PositiveInfinity as its DesiredSize"). Without a generator the
            // panel has no measurable content, so an empty size is correct.
            return new Size(0, 0);
        }

        var itemCount = generator.Items.Count;
        if (itemCount == 0)
        {
            RemoveAllRealized();
            UpdateScrollInfo(availableSize, new Size(0, 0));
            return new Size(0, 0);
        }

        // ---- cell size: fixed, or probed from a realized child ---------------
        _cellWidth = double.IsNaN(ItemWidth) ? ProbeItemWidth() : ItemWidth;
        _cellHeight = double.IsNaN(ItemHeight) ? ProbeItemHeight() : ItemHeight;
        _cellWidth = Sanitize(_cellWidth, 200);
        _cellHeight = Sanitize(_cellHeight, 300);

        var stepX = _cellWidth + SpacingX;
        var stepY = _cellHeight + SpacingY;

        // ---- columns: fit the viewport (or one long row when unbounded) ------
        var viewportWidth = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var columns = viewportWidth > 0
            ? Math.Max(1, (int)Math.Floor((viewportWidth + SpacingX) / stepX))
            : itemCount;

        var rows = (int)Math.Ceiling(itemCount / (double)columns);
        var extent = new Size(
            Math.Max(0, columns * stepX - SpacingX),
            Math.Max(0, rows * stepY - SpacingY));
        UpdateScrollInfo(availableSize, extent);

        // ---- visible window of items -----------------------------------------
        var viewportHeight = double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height;
        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / stepY));
        var visibleRows = (int)Math.Ceiling(viewportHeight / stepY) + 1;
        var startIndex = Math.Min(itemCount - 1, firstRow * columns);
        var endIndex = Math.Min(itemCount - 1, (firstRow + visibleRows) * columns - 1);

        RealizeRange(generator, startIndex, endIndex);
        _arrangePlan = new ArrangePlan(columns, stepX, stepY, startIndex);

        var desiredWidth = viewportWidth <= 0 ? extent.Width : Math.Min(viewportWidth, extent.Width);
        var desiredHeight = double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height;

        // Layout contract: DesiredSize must be finite no matter what constraint
        // came in. Clamp defensively so a degenerate constraint (or a malformed
        // NaN binding on Spacing/Item*) can never propagate upward.
        return new Size(
            double.IsFinite(desiredWidth) && desiredWidth > 0 ? desiredWidth : 0,
            double.IsFinite(desiredHeight) && desiredHeight > 0 ? desiredHeight : 0);
    }

    private readonly struct ArrangePlan(int columns, double stepX, double stepY, int startIndex)
    {
        public int Columns { get; } = columns;
        public double StepX { get; } = stepX;
        public double StepY { get; } = stepY;
        public int StartIndex { get; } = startIndex;
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
            var y = row * _arrangePlan.StepY - _offset.Y;

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

    private void RealizeRange(IItemContainerGenerator generator, int startIndex, int endIndex)
    {
        // Drop everything outside the window first (from the end so indexes stay valid).
        for (var i = _realized.Count - 1; i >= 0; i--)
        {
            var index = _realized[i];
            if (index < startIndex || index > endIndex)
            {
                generator.Remove(new GeneratorPosition(i, 0), 1);
                RemoveInternalChildRange(i, 1);
                _realized.RemoveAt(i);
            }
        }

        for (var index = startIndex; index <= endIndex; index++)
        {
            if (_realized.Contains(index))
            {
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
                }
            }

            child?.Measure(new Size(_cellWidth, _cellHeight));
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
                generator.Remove(new GeneratorPosition(i, 0), 1);
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
