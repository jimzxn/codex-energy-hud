using System.Text.Json;
using System.Xml.Linq;
using CodexHud.Core;

namespace CodexHud.Mac;

internal sealed class HudSettings
{
    public int Version { get; set; } = 1;
    public int? X { get; set; }
    public int? Y { get; set; }
    public double Scale { get; set; } = 1;
    public double PanelOpacity { get; set; } = .94;
    public bool AlwaysOnTop { get; set; } = true;
    public bool PositionLocked { get; set; }
    public bool CompletionNotifications { get; set; }
    public bool NotificationSound { get; set; }
    public HardwareScope HardwareScope { get; set; }
    public string? SelectedQuotaKey { get; set; }
    public string? CodexHome { get; set; }
    public string? CodexExecutable { get; set; }

    public void Normalize()
    {
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, .8, 2) : 1;
        PanelOpacity = double.IsFinite(PanelOpacity) ? Math.Clamp(PanelOpacity, .45, 1) : .94;
        if (!Enum.IsDefined(HardwareScope)) HardwareScope = HardwareScope.System;
    }
}

internal sealed class SettingsStore
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "CodexHud");
    public string DirectoryPath => DefaultDirectory;
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public HudSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<HudSettings>(File.ReadAllText(FilePath)) ?? new();
            settings.Normalize();
            return settings;
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

/// <summary>Opt-in next-login launch only. Never starts another copy of the current process.</summary>
internal static class LoginStartup
{
    private const string Label = "local.codexhud.macos";
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");
    public static bool IsEnabled => File.Exists(FilePath);

    public static void SetEnabled(bool enabled)
    {
        if (!enabled) { File.Delete(FilePath); return; }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !executable.Contains(".app/Contents/MacOS/", StringComparison.Ordinal)
            || !File.Exists(executable))
            throw new InvalidOperationException("请先将打包后的 Codex HUD.app 放到固定目录，再启用登录自启。");
        var plist = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                new XElement("key", "Label"), new XElement("string", Label),
                new XElement("key", "ProgramArguments"), new XElement("array", new XElement("string", executable)),
                new XElement("key", "RunAtLoad"), new XElement("true"),
                new XElement("key", "LimitLoadToSessionType"), new XElement("string", "Aqua"))));
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        plist.Save(temporary);
        File.Move(temporary, FilePath, true);
    }
}
