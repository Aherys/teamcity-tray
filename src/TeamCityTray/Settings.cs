using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamCityTray;

public sealed class Settings
{
    public string ServerUrl { get; set; } = "";
    public string TokenProtected { get; set; } = "";
    public int PollSeconds { get; set; } = 10;
    public bool OnlyMine { get; set; } = true;
    public bool LinkChildrenByNumber { get; set; } = true;
    public int KeepFinishedSeconds { get; set; } = 60;
    public bool HideWhenIdle { get; set; } = true;
    public bool Muted { get; set; }
    public string SuccessWavPath { get; set; } = "";
    public string FailureWavPath { get; set; } = "";
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    [JsonIgnore]
    public string Token
    {
        get
        {
            if (string.IsNullOrEmpty(TokenProtected))
            {
                return "";
            }
            try
            {
                var plain = ProtectedData.Unprotect(Convert.FromBase64String(TokenProtected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (Exception)
            {
                return "";
            }
        }
        set
        {
            TokenProtected = string.IsNullOrEmpty(value)
                ? ""
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TeamCityTray", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch (Exception)
        {
            // A corrupt file falls back to defaults.
        }
        return new Settings();
    }

    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
    }
}
