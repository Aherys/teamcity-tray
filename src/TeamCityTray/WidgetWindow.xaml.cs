using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace TeamCityTray;

public partial class WidgetWindow : Window
{
    private const double ScreenGap = 12;
    private bool _userPlaced;

    public event Action? PositionChanged;

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

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
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
        else if (!onGrip && row?.WebUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
