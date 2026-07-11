using System;
using Microsoft.Win32;

namespace xPasteWin.Services;

/// <summary>Chạy cùng Windows qua Registry Run key (tương đương SMAppService/login item của macOS).</summary>
public static class LaunchAtLoginService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "xPaste";

    private static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(ValueName) is string s && !string.IsNullOrEmpty(s);
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;
            if (enabled) key.SetValue(ValueName, $"\"{ExePath}\"");
            else key.DeleteValue(ValueName, false);
        }
        catch { }
    }
}
