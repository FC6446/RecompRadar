using System.Windows;
using System.Windows.Input;

namespace RecompRadar.App.Behaviors;

/// <summary>
/// Attached behavior that turns any element into a file drop target and forwards the dropped paths (string[]) to a command.
/// <see cref="IsDragOverProperty"/> is true while a file is dragged over the element, so styles can highlight it.
/// </summary>
public static class FileDrop
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(FileDrop), new PropertyMetadata(null, OnCommandChanged));

    public static readonly DependencyProperty IsDragOverProperty = DependencyProperty.RegisterAttached(
        "IsDragOver", typeof(bool), typeof(FileDrop), new PropertyMetadata(false));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    public static bool GetIsDragOver(DependencyObject element) => (bool)element.GetValue(IsDragOverProperty);

    public static void SetIsDragOver(DependencyObject element, bool value) => element.SetValue(IsDragOverProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.DragEnter -= OnDragOver;
        element.DragOver -= OnDragOver;
        element.DragLeave -= OnDragLeave;
        element.Drop -= OnDrop;
        if (e.NewValue is null)
        {
            element.AllowDrop = false;
            return;
        }

        element.AllowDrop = true;
        element.DragEnter += OnDragOver;
        element.DragOver += OnDragOver;
        element.DragLeave += OnDragLeave;
        element.Drop += OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        var element = (DependencyObject)sender;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            SetIsDragOver(element, true);
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        SetIsDragOver((DependencyObject)sender, false);
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        var element = (DependencyObject)sender;
        SetIsDragOver(element, false);
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && GetCommand(element) is { } command && command.CanExecute(files))
        {
            command.Execute(files);
        }
    }
}
