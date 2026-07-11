using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace xPasteWin.Services;

/// <summary>
/// Lấy tên hiển thị của trình duyệt mặc định (để hiện "Open in Chrome/Edge/…" ở preview URL,
/// tương đương browserName(for:) của macOS). Tra UserChoice ProgId → command → FileDescription của exe.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DefaultBrowser
{
    private static string? _cached;

    public static string Name()
    {
        if (_cached != null) return _cached;
        _cached = Resolve();
        return _cached;
    }

    private static string Resolve()
    {
        try
        {
            using var uc = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (uc?.GetValue("ProgId") is not string progId || string.IsNullOrEmpty(progId)) return "Browser";

            using var cmd = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            var exe = ExtractExe(cmd?.GetValue(null) as string);
            if (exe != null && File.Exists(exe))
            {
                var fd = FileVersionInfo.GetVersionInfo(exe).FileDescription;
                return !string.IsNullOrWhiteSpace(fd) ? fd! : Path.GetFileNameWithoutExtension(exe);
            }
        }
        catch { }
        return "Browser";
    }

    /// <summary>Rút đường dẫn exe từ chuỗi command (có thể có dấu ngoặc kép + tham số).</summary>
    private static string? ExtractExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 1) return command.Substring(1, end - 1);
            command = command[1..]; // ngoặc mở nhưng thiếu ngoặc đóng → bỏ ngoặc rồi dò .exe
        }
        int ext = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return ext > 0 ? command[..(ext + 4)] : null;
    }
}
