using System;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace TeamCityTray;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Action _onSaved;

    public SettingsWindow(Settings settings, Action onSaved)
    {
        InitializeComponent();
        _settings = settings;
        _onSaved = onSaved;

        UrlBox.Text = settings.ServerUrl;
        TokenBox.Password = settings.Token;
        PollBox.Text = settings.PollSeconds.ToString();
        KeepBox.Text = settings.KeepFinishedSeconds.ToString();
        OnlyMineBox.IsChecked = settings.OnlyMine;
        LinkBox.IsChecked = settings.LinkChildrenByNumber;
        BotBox.Text = settings.BotAccounts;
        HideIdleBox.IsChecked = settings.HideWhenIdle;
        TopBox.IsChecked = settings.AlwaysOnTop;
        StartBox.IsChecked = settings.StartWithWindows;
        SuccessBox.Text = settings.SuccessWavPath;
        FailureBox.Text = settings.FailureWavPath;
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        TestResult.Text = "Testing...";
        try
        {
            using var client = new TeamCityClient();
            client.Configure(UrlBox.Text, TokenBox.Password);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var user = await client.GetCurrentUserAsync(cts.Token);
            TestResult.Text = "Connected as " + user;
        }
        catch (Exception ex)
        {
            TestResult.Text = "Failed: " + ex.Message;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.ServerUrl = UrlBox.Text.Trim();
        _settings.Token = TokenBox.Password;
        _settings.PollSeconds = Math.Clamp(ParseInt(PollBox.Text, 10), 3, 3600);
        _settings.KeepFinishedSeconds = Math.Clamp(ParseInt(KeepBox.Text, 60), 0, 3600);
        _settings.OnlyMine = OnlyMineBox.IsChecked == true;
        _settings.LinkChildrenByNumber = LinkBox.IsChecked == true;
        _settings.BotAccounts = BotBox.Text.Trim();
        _settings.HideWhenIdle = HideIdleBox.IsChecked == true;
        _settings.AlwaysOnTop = TopBox.IsChecked == true;
        _settings.StartWithWindows = StartBox.IsChecked == true;
        _settings.SuccessWavPath = SuccessBox.Text.Trim();
        _settings.FailureWavPath = FailureBox.Text.Trim();
        SettingsStore.Save(_settings);
        _onSaved();
        Close();
    }

    private static int ParseInt(string text, int fallback) => int.TryParse(text, out var v) ? v : fallback;

    private void OnBrowseSuccess(object sender, RoutedEventArgs e) => Browse(SuccessBox);

    private void OnBrowseFailure(object sender, RoutedEventArgs e) => Browse(FailureBox);

    private static void Browse(System.Windows.Controls.TextBox box)
    {
        var dialog = new OpenFileDialog { Filter = "WAV files (*.wav)|*.wav" };
        if (dialog.ShowDialog() == true)
        {
            box.Text = dialog.FileName;
        }
    }
}
