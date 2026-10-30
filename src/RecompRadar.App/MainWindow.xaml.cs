using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RecompRadar.App.Services;
using RecompRadar.App.ViewModels;

namespace RecompRadar.App;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;

    public MainWindow(MainViewModel viewModel, SettingsService settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settings = settings;
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    /// <summary>Fades and slides the new page in. Skipped when animations are switched off in the settings.</summary>
    private void PageHost_TargetUpdated(object? sender, DataTransferEventArgs e)
    {
        if (!_settings.Current.EnableAnimations || PageHost.RenderTransform is not TranslateTransform transform)
        {
            PageHost.Opacity = 1;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
    }

    private void UseDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 2004+ / Windows 11). Failure just leaves the default title bar.
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows versions: keep the standard title bar.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
