using System.IO;
using System.Text.Json;

namespace SpecIQ;

public static class SpecIQSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpecIQ", "settings.json");

    private static Dictionary<string, string> _data = [];

    static SpecIQSettings() => Load();

    public static string? CinebenchPath
    {
        get => Get("CinebenchPath");
        set => Set("CinebenchPath", value);
    }

    public static string? Geekbench6Path
    {
        get => Get("Geekbench6Path");
        set => Set("Geekbench6Path", value);
    }

    public static string? BanffPath
    {
        get => Get("BanffPath");
        set => Set("BanffPath", value);
    }

    private static string? Get(string key) =>
        _data.TryGetValue(key, out var v) ? v : null;

    private static void Set(string key, string? value)
    {
        if (value == null) _data.Remove(key);
        else _data[key] = value;
        Save();
    }

    private static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                _data = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(SettingsPath)) ?? [];
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
