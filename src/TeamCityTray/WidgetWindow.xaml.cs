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

    public WidgetWindow(WidgetViewModel viewModel, double? left, double? top)
    {
        InitializeComponent();
        DataContext = viewModel;

        if (left is { } l && top is { } t && IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
            _userPlaced = true;
        }
        SizeChanged += (_, _) => KeepInPlace();
    }

    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 20 && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40
        && top >= SystemParameters.VirtualScreenTop - 20 && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20;

    private void KeepInPlace()
    {
        var area = SystemParameters.WorkArea;
        if (!_userPlaced)
        {
            Left = area.Right - ActualWidth - ScreenGap;
            Top = area.Bottom - ActualHeight - ScreenGap;
        }
        else if (Top + ActualHeight > area.Bottom)
        {
            Top = Math.Max(area.Top, area.Bottom - ActualHeight);
        }
    }

    public double? SavedLeft => _userPlaced ? Left : null;
    public double? SavedTop => _userPlaced ? Top : null;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as RowViewModel;
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
        else if (row?.WebUrl is { Length: > 0 } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
