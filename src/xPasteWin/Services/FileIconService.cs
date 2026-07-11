using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace xPasteWin.Services;

/// <summary>
/// Trích icon hệ thống của file/folder (tương đương NSWorkspace.icon(forFile:) của macOS) và lưu ra PNG
/// để bind vào card. Dùng SHGetFileInfo với USEFILEATTRIBUTES → lấy icon theo LOẠI (không chạm ổ đĩa),
/// cache theo phần mở rộng / folder nên nhanh và tái dùng giữa các file cùng loại.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileIconService
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new();
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xPaste", "fileicons");

    /// <summary>Đường dẫn PNG icon của file/folder tại <paramref name="path"/>; null nếu lỗi.</summary>
    public static string? GetIconPng(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        bool isDir = Directory.Exists(path);
        // Key cache theo loại: folder chung một icon; file theo phần mở rộng (không có ext → theo tên).
        string key = isDir ? "<dir>" : Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) key = "<noext:" + Path.GetFileName(path).ToLowerInvariant() + ">";
        return Cache.GetOrAdd(key, _ => Extract(path!, isDir, key));
    }

    private static string? Extract(string path, bool isDir, string key)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            Directory.CreateDirectory(CacheDir);
            var png = Path.Combine(CacheDir, Hash(key) + ".png");
            if (File.Exists(png) && new FileInfo(png).Length > 0) return png;

            var shfi = new SHFILEINFO();
            uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            var res = SHGetFileInfo(path, attr, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
            hIcon = shfi.hIcon;
            if (res == IntPtr.Zero || hIcon == IntPtr.Zero) return null;

            using var icon = Icon.FromHandle(hIcon);
            using var bmp = icon.ToBitmap();
            bmp.Save(png, ImageFormat.Png);
            return png;
        }
        catch { return null; }
        finally { if (hIcon != IntPtr.Zero) DestroyIcon(hIcon); }
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    private const uint SHGFI_ICON = 0x000000100, SHGFI_LARGEICON = 0x000000000, SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080, FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
}
