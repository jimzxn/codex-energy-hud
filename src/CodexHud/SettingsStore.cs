using System.IO;
using System.Text.Json;
using CodexHud.Core;

namespace CodexHud;

public sealed class HudSettings
{
    public int Version { get; set; } = 1;
    public int? X { get; set; }
    public int? Y { get; set; }
    public double Scale { get; set; } = 1;
    public double PanelOpacity { get; set; } = .94;
    public bool AlwaysOnTop { get; set; } = true;
    public bool PositionLocked { get; set; }
    public bool AttentionNotifications { get; set; } = true;
    public bool CompletionNotifications { get; set; }
    public bool NotificationSound { get; set; }
    public bool ShowSessionCost { get; set; } = true;
    public HardwareScope HardwareScope { get; set; }
    public string? SelectedQuotaKey { get; set; }
    public string? CodexHome { get; set; }
    public void Normalize()
    {
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, .8, 2) : 1;
        PanelOpacity = double.IsFinite(PanelOpacity) ? Math.Clamp(PanelOpacity, .45, 1) : .94;
        if (!Enum.IsDefined(HardwareScope)) HardwareScope = HardwareScope.System;
    }
}

public sealed class SettingsStore
{
    public string DirectoryPath { get; }
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public SettingsStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexHud");
    public HudSettings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<HudSettings>(File.ReadAllText(FilePath)) ?? new();
            s.Normalize();
            return s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public bool Save(HudSettings settings)
    {
        try
        {
            settings.Normalize();
            Directory.CreateDirectory(DirectoryPath);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
