using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LocalTranslate;

public sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public string Language { get; set; } = "auto"; // "auto", "tr" or "en"
    public string Target { get; set; } = "tr";
    public bool Hover { get; set; } = true;
    public bool Selection { get; set; } = true;
    public int HoverDelay { get; set; } = 500;
    public bool OcrFallback { get; set; } = true;
    public bool ClipboardFallback { get; set; } = true;
    public string Server { get; set; } = "http://localhost:5000";
    public string ApiKey { get; set; } = "";
    // Ctrl+Alt+letter is AltGr+letter on Turkish/international layouts (₺, €, ß…):
    // registering those would stop the user from typing them. F-keys have no AltGr characters.
    public string Hotkey { get; set; } = "Ctrl+Alt+F9";
    public string ScreenHotkey { get; set; } = "Ctrl+Alt+F10";
    public string RegionHotkey { get; set; } = "Ctrl+Alt+F11";
    public int Version { get; set; }
    const int CurrentVersion = 2;

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalTranslate");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // "Ctrl+Alt+F9", not "Ctrl+Alt…"
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                if (s.Version < 2)
                {
                    // Old defaults clashed with AltGr+T/E/S (₺, €, ß).
                    if (s.Hotkey == "Ctrl+Alt+T") s.Hotkey = "Ctrl+Alt+F9";
                    if (s.ScreenHotkey == "Ctrl+Alt+E") s.ScreenHotkey = "Ctrl+Alt+F10";
                    if (s.RegionHotkey == "Ctrl+Alt+S") s.RegionHotkey = "Ctrl+Alt+F11";
                }
                return s;
            }
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Version = CurrentVersion;
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    public Settings Clone() => (Settings)MemberwiseClone();

    // "Start with Windows" lives in the registry, not in the JSON file.
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "LocalTranslate";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunName, false);
        }
    }
}
