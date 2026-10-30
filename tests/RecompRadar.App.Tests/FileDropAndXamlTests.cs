using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using RecompRadar.App.Behaviors;
using RecompRadar.App.ViewModels;
using RecompRadar.TestSupport;

namespace RecompRadar.App.Tests;

[Collection("WPF")]
public class FileDropTests
{
    /// <summary>
    /// WPF has no public constructor for drag events, so real ones are created through the internal constructor.
    /// The events then travel through the normal routed-event system into the behavior under test.
    /// </summary>
    private static DragEventArgs CreateDragArgs(RoutedEvent routedEvent, IDataObject data, UIElement target)
    {
        var ctor = typeof(DragEventArgs)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 5);
        var args = (DragEventArgs)ctor.Invoke([data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Copy, target, new Point(1, 1)]);
        args.RoutedEvent = routedEvent;
        return args;
    }

    private static DataObject Files(params string[] paths)
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths);
        return data;
    }

    private static void Raise(UIElement target, RoutedEvent routedEvent, IDataObject data) =>
        target.RaiseEvent(CreateDragArgs(routedEvent, data, target));

    [Fact]
    public void Dropped_files_reach_the_command_and_the_drag_highlight_follows_the_drag()
    {
        Sta.Run(() =>
        {
            string[]? received = null;
            var command = new RelayCommand<string[]>(files => received = files);
            var zone = new Border();
            FileDrop.SetCommand(zone, command);
            var data = Files(@"C:\a\first.elf", @"C:\a\second.elf");

            Assert.True(zone.AllowDrop);
            Assert.False(FileDrop.GetIsDragOver(zone));

            Raise(zone, DragDrop.DragEnterEvent, data);
            Assert.True(FileDrop.GetIsDragOver(zone));
            Raise(zone, DragDrop.DragOverEvent, data);
            Assert.True(FileDrop.GetIsDragOver(zone));
            Raise(zone, DragDrop.DragLeaveEvent, data);
            Assert.False(FileDrop.GetIsDragOver(zone));

            Raise(zone, DragDrop.DragEnterEvent, data);
            Raise(zone, DragDrop.DropEvent, data);

            Assert.False(FileDrop.GetIsDragOver(zone));
            Assert.Equal([@"C:\a\first.elf", @"C:\a\second.elf"], received!);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Dragging_something_that_is_not_a_file_is_refused_and_never_reaches_the_command()
    {
        Sta.Run(() =>
        {
            var called = 0;
            var zone = new Border();
            FileDrop.SetCommand(zone, new RelayCommand<string[]>(_ => called++));
            var text = new DataObject();
            text.SetData(DataFormats.Text, "just text");

            var over = CreateDragArgs(DragDrop.DragOverEvent, text, zone);
            zone.RaiseEvent(over);
            Raise(zone, DragDrop.DropEvent, text);

            Assert.Equal(DragDropEffects.None, over.Effects);
            Assert.False(FileDrop.GetIsDragOver(zone));
            Assert.Equal(0, called);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void A_disabled_command_is_not_invoked_and_removing_the_command_stops_accepting_drops()
    {
        Sta.Run(() =>
        {
            var called = 0;
            var command = new RelayCommand<string[]>(_ => called++, _ => false);
            var zone = new Border();
            FileDrop.SetCommand(zone, command);

            Raise(zone, DragDrop.DropEvent, Files(@"C:\x.elf"));
            Assert.Equal(0, called);

            FileDrop.SetCommand(zone, null);
            Assert.False(zone.AllowDrop);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Dropping_on_a_view_model_command_runs_a_real_analysis()
    {
        Sta.Run(async () =>
        {
            using var h = new Harness();
            var zone = new Border();
            FileDrop.SetCommand(zone, h.Analyzer.DropFilesCommand);

            Raise(zone, DragDrop.DropEvent, Files(h.Sample("poc_mips32_be_sum.elf")));
            await Harness.WaitUntilAsync(() => h.Analyzer.HasResult && !h.Session.IsBusy);

            Assert.Equal("poc_mips32_be_sum.elf", h.Analyzer.FileName);
            Assert.True(h.Analyzer.IsParsed);
        });
    }
}

[Collection("WPF")]
public class XamlSmokeTests
{
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message) => WriteLine(message);

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Messages.Add(message);
            }
        }
    }

    /// <summary>
    /// Creates the real application resources and main window, realises every page with real analysis data and fails on
    /// any XAML parse error, missing resource or binding error. Only one Application can exist per process, so this
    /// is a single test.
    /// </summary>
    [Fact]
    public void Every_page_loads_and_binds_without_errors_for_every_sample()
    {
        Sta.Run(async () =>
        {
            var app = new App();
            app.InitializeComponent();

            var listener = new BindingErrorListener();
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

            // Prove the listener works: a deliberately broken binding must be reported, otherwise "no errors" would mean nothing.
            var broken = new TextBlock();
            broken.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("NoSuchProperty") { Source = new object() });
            Assert.NotEmpty(listener.Messages);
            listener.Messages.Clear();

            using var h = new Harness();
            var window = new MainWindow(h.Main, h.Settings);
            var root = (UIElement)window.Content;
            var size = new Size(1320, 840);

            void Layout()
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                root.UpdateLayout();
            }

            var pageKeys = new[] { "dashboard", "analyzer", "architecture", "dependencies", "report", "projects", "settings" };
            foreach (var sample in SampleFiles.All())
            {
                await h.Session.AnalyzeAsync(h.Sample(sample.FileName));
                foreach (var key in pageKeys)
                {
                    h.Main.Navigate(key);
                    Layout();
                    Assert.Equal(key, h.Main.CurrentPage.Key);
                }
            }

            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            Assert.True(listener.Messages.Count == 0, "Binding problems:\n" + string.Join("\n", listener.Messages.Take(10)));

            // The page area really shows a view for the current page.
            h.Main.Navigate("report");
            Layout();
            Assert.NotNull(FindVisualChild<RecompRadar.App.Views.ReportView>(root));
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
