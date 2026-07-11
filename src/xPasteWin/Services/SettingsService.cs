using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace xPasteWin.Services;

public interface ISettings
{
    T Get<T>(string key, T fallback);
    void Set<T>(string key, T value);
}

public sealed class SettingsService : ISettings
{
    private readonly string _path;
    private readonly Dictionary<string, JsonElement> _map;
    private readonly object _lock = new();

    public SettingsService(string? dir = null)
    {
        dir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xPaste");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        _map = LoadMap(_path);
    }

    // Chịu lỗi file hỏng/ghi dở (mất điện giữa lúc Set): không crash khởi động, dùng settings rỗng.
    private static Dictionary<string, JsonElement> LoadMap(string path)
    {
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); }
    }

    public T Get<T>(string key, T fallback)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var el))
            {
                try { return el.Deserialize<T>()!; } catch { return fallback; }
            }
            return fallback;
        }
    }

    public void Set<T>(string key, T value)
    {
        lock (_lock)
        {
            _map[key] = JsonSerializer.SerializeToElement(value);
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_map)); } catch { /* đĩa đầy/khóa file: giữ giá trị trong RAM */ }
        }
    }
}
