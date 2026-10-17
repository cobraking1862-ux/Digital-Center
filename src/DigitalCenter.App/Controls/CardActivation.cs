using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DigitalCenter.App.Controls;

/// <summary>
/// Shared card-activation behaviour for poster and tile cards.
///
/// One left click SELECTS the card, a second click (double click) OPENS it, and
/// Enter or Space opens the focused card — so the card is reachable by keyboard
/// and announces itself to UI Automation like any other selectable item.
///
/// Clicks that start inside an interactive child (a button such as
/// "Add to Cart", a text box, a combo box, a scroll bar or a thumb) are left
/// completely alone, which is what keeps an in-card action from also navigating
/// the card.
///
/// The behaviour is attached with XAML attributes only; cards stay plain
/// <c>Border</c> elements, so no screen needs its own mouse handlers and every
/// grid in the application behaves identically.
/// </summary>
public static class CardActivation
{
    /// <summary>Turns the behaviour on for an element (and makes it focusable).</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(CardActivation),
            new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>Executed with the card's data context on a single left click.</summary>
    public static readonly DependencyProperty SelectCommandProperty =
        DependencyProperty.RegisterAttached(
            "SelectCommand",
            typeof(ICommand),
            typeof(CardActivation),
            new PropertyMetadata(null));

    /// <summary>Executed with the card's data context on a double click, Enter or Space.</summary>
    public static readonly DependencyProperty OpenCommandProperty =
        DependencyProperty.RegisterAttached(
            "OpenCommand",
            typeof(ICommand),
            typeof(CardActivation),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static ICommand? GetSelectCommand(DependencyObject element) => (ICommand?)element.GetValue(SelectCommandProperty);

    public static void SetSelectCommand(DependencyObject element, ICommand? value) => element.SetValue(SelectCommandProperty, value);

    public static ICommand? GetOpenCommand(DependencyObject element) => (ICommand?)element.GetValue(OpenCommandProperty);

    public static void SetOpenCommand(DependencyObject element, ICommand? value) => element.SetValue(OpenCommandProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if (e.NewValue is true)
        {
            // Keyboard activation requires the card itself to take focus.
            if (element is FrameworkElement frameworkElement && !frameworkElement.Focusable)
            {
                frameworkElement.Focusable = true;
            }

            element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            element.KeyDown += OnKeyDown;
        }
        else
        {
            element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            element.KeyDown -= OnKeyDown;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not UIElement card || StartsInsideInteractiveControl(e.OriginalSource as DependencyObject, card))
        {
            return; // The click belongs to a control inside the card.
        }

        var dataContext = (card as FrameworkElement)?.DataContext;

        if (e.ClickCount >= 2)
        {
            // Second click of a double click: open. Handled so the click never
            // reaches the card's own children or the scroll viewer behind it.
            if (TryExecute(GetOpenCommand(card), dataContext))
            {
                e.Handled = true;
            }

            return;
        }

        // First click: select only. Deliberately not marked handled — WPF uses
        // this event to move keyboard focus onto the card.
        TryExecute(GetSelectCommand(card), dataContext);
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not UIElement card || e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        if (TryExecute(GetOpenCommand(card), (card as FrameworkElement)?.DataContext))
        {
            e.Handled = true;
        }
    }

    private static bool TryExecute(ICommand? command, object? parameter)
    {
        if (command is null || !command.CanExecute(parameter))
        {
            return false;
        }

        command.Execute(parameter);
        return true;
    }

    /// <summary>
    /// True when the element that received the click is the card's own
    /// interactive child (or sits inside one) rather than the card surface.
    /// </summary>
    private static bool StartsInsideInteractiveControl(DependencyObject? source, UIElement card)
    {
        var node = source;
        while (node is not null && !ReferenceEquals(node, card))
        {
            if (node is ButtonBase or TextBoxBase or ComboBox or ScrollBar or Thumb)
            {
                return true;
            }

            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
