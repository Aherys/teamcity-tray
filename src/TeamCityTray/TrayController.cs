using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TeamCityTray;

public sealed class TrayController : IDisposable
{
    private readonly Settings _settings = SettingsStore.Load();
    private readonly WidgetViewModel _viewModel = new();
    private readonly SoundService _sound;
    private readonly Poller _poller;
    private readonly WidgetWindow _widget;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem = new("Show/Hide widget");
    private readonly ToolStripMenuItem _muteItem = new("Mute sounds");
    private readonly ToolStripMenuItem _topItem = new("Always on top");
    private readonly ToolStripMenuItem _idleItem = new("Hide when idle");
    private readonly ToolStripMenuItem _startItem = new("Start with Windows");
    private SettingsWindow? _settingsWindow;
    private bool _userVisible = true;
    private IReadOnlyList<RowData> _rows = Array.Empty<RowData>();
    private bool _offline;

    public TrayController()
    {
        _sound = new SoundService(_settings);
        _poller = new Poller(_settings, _sound);
        _widget = new WidgetWindow(_viewModel, _settings.WindowLeft, _settings.WindowTop, _settings.AlwaysOnTop);
        _widget.PositionChanged += () =>
        {
            _settings.WindowLeft = _widget.SavedLeft;
            _settings.WindowTop = _widget.SavedTop;
            SettingsStore.Save(_settings);
        };

        _toggleItem.Click += (_, _) => ToggleWidget();
        _muteItem.CheckOnClick = true;
        _muteItem.Checked = _settings.Muted;
        _muteItem.Click += (_, _) =>
        {
            _settings.Muted = _muteItem.Checked;
            SettingsStore.Save(_settings);
        };

        WireCheckItem(_topItem, v =>
        {
            _settings.AlwaysOnTop = v;
            _widget.Topmost = v;
        });
        WireCheckItem(_idleItem, v =>
        {
            _settings.HideWhenIdle = v;
            UpdateVisibility();
        });
        WireCheckItem(_startItem, v =>
        {
            _settings.StartWithWindows = v;
            Autostart.Apply(v);
        });
        SyncMenu();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add("Settings", null, (_, _) => ShowSettings());
        menu.Items.Add(_muteItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_topItem);
        menu.Items.Add(_idleItem);
        menu.Items.Add(_startItem);
        menu.Items.Add("Reset position", null, (_, _) => _widget.ResetPosition());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _icon = new NotifyIcon { Icon = CreateIcon(), Text = "TeamCity Tray", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleWidget();
            }
        };
    }

    private void WireCheckItem(ToolStripMenuItem item, Action<bool> apply)
    {
        item.CheckOnClick = true;
        item.Click += (_, _) =>
        {
            apply(item.Checked);
            SettingsStore.Save(_settings);
        };
    }

    private void SyncMenu()
    {
        _topItem.Checked = _settings.AlwaysOnTop;
        _idleItem.Checked = _settings.HideWhenIdle;
        _startItem.Checked = _settings.StartWithWindows;
    }

    private void OnSettingsSaved()
    {
        SyncMenu();
        _widget.Topmost = _settings.AlwaysOnTop;
        Autostart.Apply(_settings.StartWithWindows);
        UpdateVisibility();
    }

    public void Start()
    {
        Autostart.Apply(_settings.StartWithWindows);
        _poller.Updated += (rows, offline) =>
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Apply(rows, offline));
        _poller.Start();
        UpdateVisibility();

        if (string.IsNullOrWhiteSpace(_settings.ServerUrl))
        {
            ShowSettings();
        }
    }

    private void Apply(IReadOnlyList<RowData> rows, bool offline)
    {
        _rows = rows;
        _offline = offline;
        _viewModel.Apply(rows, offline);
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        var hasContent = _rows.Count > 0 || _offline || !_settings.HideWhenIdle;
        if (_userVisible && hasContent)
        {
            if (!_widget.IsVisible)
            {
                _widget.Show();
            }
        }
        else if (_widget.IsVisible)
        {
            _widget.Hide();
        }
    }

    private void ToggleWidget()
    {
        _userVisible = !_userVisible;
        UpdateVisibility();
    }

    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, OnSettingsSaved);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var back = new SolidBrush(Color.FromArgb(30, 33, 40));
            g.FillEllipse(back, 1, 1, 30, 30);
            using var ring = new Pen(Color.FromArgb(76, 154, 255), 4);
            g.DrawArc(ring, 6, 6, 20, 20, -90, 260);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _poller.Dispose();
        _widget.Close();
    }
}
