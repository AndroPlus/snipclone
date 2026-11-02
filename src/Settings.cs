using System.IO;
using System.Text.Json;

namespace SnipClone;

public enum SnipMode { Rectangle, Window, FullScreen, Freeform, ColorPicker }

/// <summary>User settings, stored as JSON in %APPDATA%\SnipClone\settings.json.</summary>
public sealed class Settings
{
    public SnipMode Mode { get; set; } = SnipMode.Rectangle;
    public int Delay { get; set; }
    public string ColorFormat { get; set; } = "HEX";
    public bool AutoCopy { get; set; } = true;
    public bool AutoSave { get; set; } = true;
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    public bool Outline { get; set; }
    public bool ConfirmUnsaved { get; set; } = true;

    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnipClone", "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* settings are a convenience; never fail the app over them */ }
    }
}
