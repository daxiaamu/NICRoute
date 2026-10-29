using System.Text.Json;

namespace NICRoute.Core;

public sealed class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public SettingsStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NICRoute");
    }
    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        return Read(SettingsPath);
    }
    public static AppSettings Read(string path)
    {
        var result = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException("配置文件为空。");
        result.Validate();
        return result;
    }
    public void Save(AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(DirectoryPath);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, SettingsPath, true);
    }
    public static AppSettings Clone(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(
        JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
}
