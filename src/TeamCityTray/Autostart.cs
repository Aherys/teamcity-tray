using System;
using Microsoft.Win32;

namespace TeamCityTray;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TeamCityTray";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { Length: > 0 } path)
            {
                key.SetValue(ValueName, "\"" + path + "\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception)
        {
            // Autostart is optional: ignore registry errors.
        }
    }
}
