using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace xPasteWin.Models;

public enum ClipboardContentType { Text, Url, Image, File, Folder }

public class ClipboardItem
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("type")] public ClipboardContentType Type { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("imageSize")] public int? ImageSize { get; set; }
    [JsonPropertyName("imageWidth")] public int? ImageWidth { get; set; }
    [JsonPropertyName("imageHeight")] public int? ImageHeight { get; set; }
    [JsonPropertyName("imageHash")] public string? ImageHash { get; set; }
    [JsonPropertyName("filePaths")] public string[]? FilePaths { get; set; }
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    [JsonPropertyName("isPinned")] public bool IsPinned { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("sourceApp")] public string? SourceApp { get; set; }
    [JsonPropertyName("richData")] public byte[]? RichData { get; set; }
    [JsonPropertyName("richType")] public string? RichType { get; set; }

    // KHÔNG serialize: ảnh lưu ra file .jpg riêng, chỉ giữ trong RAM.
    [JsonIgnore] public byte[]? ImageData { get; set; }

    [JsonIgnore]
    public string DisplayText => Type switch
    {
        ClipboardContentType.Text or ClipboardContentType.Url => Text ?? "",
        ClipboardContentType.Image => Label ?? "Image",
        _ => FilePaths is { Length: > 0 }
            ? string.Join(", ", FilePaths.Select(Path.GetFileName))
            : (Type == ClipboardContentType.Folder ? "Folder" : "File"),
    };

    public static string MakeHash(byte[] data)
    {
        string Hex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));
        var prefix = Hex(data.Take(16).ToArray());
        var suffix = Hex(data.Skip(Math.Max(0, data.Length - 16)).ToArray());
        return $"{data.Length}-{prefix}-{suffix}";
    }

    public static bool AllDirectories(string[] paths) =>
        paths.All(p => Directory.Exists(p) && !File.Exists(p));
}
