using System;
using System.Diagnostics;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TeamCityTray;

public partial class WidgetWindow : Window
{
    private const double ScreenGap = 12;
    private bool _userPlaced;

    private const uint FlashAll = 3;
    private const uint FlashUntilForeground = 12;
    private int _pulseId;

    public event Action? PositionChanged;
    public event Action<long>? DismissRequested;
    public event Action<RowViewModel>? RerunRequested;

    public WidgetWindow(WidgetViewModel viewModel, double? left, double? top, bool alwaysOnTop)
    {
        InitializeComponent();
        DataContext = viewModel;
        Topmost = alwaysOnTop;

        if (left is { } l && top is { } t && IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
            _userPlaced = true;
        }
        SizeChanged += (_, _) => KeepInPlace();
    }

    private static bool IsOnScreen(double left, double top) =>
        left + 40 >= SystemParameters.VirtualScreenLeft && left + 40 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
        && top + 10 >= SystemParameters.VirtualScreenTop && top + 10 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    public void ResetPosition()
    {
        _userPlaced = false;
        KeepInPlace();
        PositionChanged?.Invoke();
    }

    private void KeepInPlace()
    {
        var area = SystemParameters.WorkArea;
        if (!_userPlaced)
        {
            Left = area.Right - ActualWidth - ScreenGap;
            Top = area.Bottom - ActualHeight - ScreenGap;
        }
        else
        {
            var minLeft = SystemParameters.VirtualScreenLeft;
            var minTop = SystemParameters.VirtualScreenTop;
            var maxLeft = minLeft + SystemParameters.VirtualScreenWidth - ActualWidth;
            var maxTop = minTop + SystemParameters.VirtualScreenHeight - ActualHeight;
            Left = Math.Clamp(Left, minLeft, Math.Max(minLeft, maxLeft));
            Top = Math.Clamp(Top, minTop, Math.Max(minTop, maxTop));
        }
    }

    public double? SavedLeft => _userPlaced ? Left : null;
    public double? SavedTop => _userPlaced ? Top : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    // Flash the taskbar entry (weak without one) and pulse a red border for 5 s.
    public void NotifyFailure()
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = new WindowInteropHelper(this).EnsureHandle(),
            Flags = FlashAll | FlashUntilForeground,
            Count = uint.MaxValue,
        };
        FlashWindowEx(ref info);

        var id = ++_pulseId;
        var brush = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
        Frame.BorderBrush = brush;
        Frame.BorderThickness = new Thickness(2);
        var animation = new ColorAnimation(Color.FromRgb(0xF8, 0x51, 0x49), Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(500))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(TimeSpan.FromSeconds(5)),
        };
        animation.Completed += (_, _) =>
        {
            if (id == _pulseId)
            {
                Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
                Frame.BorderThickness = new Thickness(1);
            }
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    private static RowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as RowViewModel;

    private static bool IsOverButton(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static void OpenUrl(string? url)
    {
        if (url is { Length: > 0 } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenUrl(RowOf(sender)?.WebUrl);

    private void OnRerunClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            RerunRequested?.Invoke(row);
        }
    }

    private void OnDismissClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            DismissRequested?.Invoke(row.Id);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOverButton(e.OriginalSource))
        {
            return;
        }
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as RowViewModel;
        var onGrip = Grip.IsMouseOver;
        var (left, top) = (Left, Top);
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (Left != left || Top != top)
        {
            _userPlaced = true;
            PositionChanged?.Invoke();
        }
        else if (!onGrip)
        {
            OpenUrl(row?.WebUrl);
        }
    }
}
