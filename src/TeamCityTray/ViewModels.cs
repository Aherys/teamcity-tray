using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace TeamCityTray;

public sealed class RowViewModel : INotifyPropertyChanged
{
    private static readonly Brush Gray = Freeze("#7F8795");
    private static readonly Brush Blue = Freeze("#4C9AFF");
    private static readonly Brush Green = Freeze("#3FB950");
    private static readonly Brush Red = Freeze("#F85149");

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; private set; }
    public string Name { get; private set; } = "";
    public string Number { get; private set; } = "";
    public int Percent { get; private set; }
    public string PercentText { get; private set; } = "";
    public string ElapsedText { get; private set; } = "";
    public Thickness Indent { get; private set; }
    public Brush DotBrush { get; private set; } = Gray;
    public Brush BarBrush { get; private set; } = Blue;
    public bool ShowBar { get; private set; }
    public string? WebUrl { get; private set; }
    public bool ShowDot { get; private set; } = true;
    public string Glyph { get; private set; } = "";
    public bool CanRerun { get; private set; }
    public bool CanDismiss { get; private set; }
    public string ToolTip { get; private set; } = "";

    public RowViewModel(RowData data) => Update(data);

    public void Update(RowData d)
    {
        Id = d.Id;
        Name = d.Name;
        Number = d.Number.Length > 0 ? "#" + d.Number : "";
        Percent = d.Percent;
        ShowBar = d.State != RowState.Queued;
        PercentText = d.State == RowState.Queued ? "queued" : d.Percent + "%";
        ElapsedText = d.State == RowState.Queued ? "" : FormatElapsed(d.ElapsedSeconds);
        Indent = new Thickness(d.Depth * 12, 0, 0, 0);
        (DotBrush, BarBrush) = d.State switch
        {
            RowState.Queued => (Gray, Gray),
            RowState.Running => (Blue, Blue),
            RowState.Success => (Green, Green),
            _ => (Red, Red),
        };
        var finished = d.State is RowState.Success or RowState.Failure;
        ShowDot = !finished;
        Glyph = d.State switch { RowState.Success => "\u2713", RowState.Failure => "\u2717", _ => "" };
        CanRerun = finished;
        CanDismiss = d.State == RowState.Failure;
        WebUrl = d.WebUrl;
        ToolTip = d.ToolTip;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static string FormatElapsed(int seconds)
    {
        if (seconds < 60)
        {
            return seconds + "s";
        }
        if (seconds < 3600)
        {
            return seconds / 60 + "m";
        }
        return $"{seconds / 3600}h{seconds % 3600 / 60:00}";
    }

    private static Brush Freeze(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}

public sealed class WidgetViewModel : INotifyPropertyChanged
{
    private bool _offline;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RowViewModel> Rows { get; } = new();

    public bool IsOffline
    {
        get => _offline;
        private set
        {
            _offline = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOffline)));
        }
    }

    public bool IsIdle => Rows.Count == 0 && !IsOffline;

    public void Apply(IReadOnlyList<RowData> rows, bool offline)
    {
        if (Rows.Select(r => r.Id).SequenceEqual(rows.Select(r => r.Id)))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                Rows[i].Update(rows[i]);
            }
        }
        else
        {
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(new RowViewModel(row));
            }
        }
        IsOffline = offline;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIdle)));
    }
}
