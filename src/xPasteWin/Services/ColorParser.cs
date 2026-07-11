using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.UI;

namespace xPasteWin.Services;

/// <summary>
/// Nhận diện text là mã màu (#hex, rgb()/rgba(), hsl()/hsla()) → Color, để card hiện ô màu
/// (tương đương detectedColor/parseHex/parseRGB/parseHSL của ClipboardItemCard macOS).
/// </summary>
public static class ColorParser
{
    public static Color? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return ParseHex(s) ?? ParseRgb(s) ?? ParseHsl(s);
    }

    private static Color? ParseHex(string s)
    {
        if (!s.StartsWith('#')) return null;
        var hex = s[1..];
        if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length is not (6 or 8) || !hex.All(Uri.IsHexDigit)) return null;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
        return hex.Length == 6
            ? Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8));
    }

    private static readonly Regex RgbRe = new(
        @"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})(?:\s*,\s*([\d.]+))?\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HslRe = new(
        @"^hsla?\(\s*([\d.]+)\s*,\s*([\d.]+)%\s*,\s*([\d.]+)%(?:\s*,\s*([\d.]+))?\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Color? ParseRgb(string s)
    {
        var m = RgbRe.Match(s);
        if (!m.Success) return null;
        if (!TryByte(m.Groups[1].Value, out var r) || !TryByte(m.Groups[2].Value, out var g)
            || !TryByte(m.Groups[3].Value, out var b)) return null;
        return Color.FromArgb(ParseAlpha(m.Groups[4].Value), r, g, b);
    }

    private static Color? ParseHsl(string s)
    {
        var m = HslRe.Match(s);
        if (!m.Success) return null;
        if (!TryD(m.Groups[1].Value, out var h) || !TryD(m.Groups[2].Value, out var sat)
            || !TryD(m.Groups[3].Value, out var l)) return null;
        return HslToColor(h / 360.0, sat / 100.0, l / 100.0, ParseAlpha(m.Groups[4].Value));
    }

    private static byte ParseAlpha(string s) =>
        string.IsNullOrEmpty(s) ? (byte)255
        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            ? (byte)Math.Clamp(Math.Round(a * 255), 0, 255) : (byte)255;

    private static bool TryByte(string s, out byte b)
    {
        b = 0;
        if (int.TryParse(s, out var v) && v is >= 0 and <= 255) { b = (byte)v; return true; }
        return false;
    }

    private static bool TryD(string s, out double d) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    private static Color HslToColor(double h, double s, double l, byte a)
    {
        double r, g, b;
        if (s == 0) { r = g = b = l; }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = Hue2Rgb(p, q, h + 1.0 / 3);
            g = Hue2Rgb(p, q, h);
            b = Hue2Rgb(p, q, h - 1.0 / 3);
        }
        return Color.FromArgb(a, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double Hue2Rgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    /// <summary>Màu sáng (để chọn chữ tối/sáng) — luminance có trọng số &gt; 0.5.</summary>
    public static bool IsLight(Color c) =>
        (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 > 0.5;
}
