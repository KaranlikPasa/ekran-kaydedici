using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace EkranKaydedici.Models;

public sealed class AppConfig
{
    public static readonly int[] ReplayDurationOptions = [15, 30, 60, 90];

    public bool ReplayEnabled { get; set; } = true;
    public int ReplaySeconds { get; set; } = 30;
    public bool SystemAudio { get; set; } = true;
    public bool MicAudio { get; set; } = false;
    public int Fps { get; set; } = 30;
    public bool RunAtStartup { get; set; } = true;

    public static string ConfigDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EkranKaydedici");

    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");

    public static string OutputDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "EkranKayitlari");

    public static AppConfig Load(string? customPath = null)
    {
        var path = customPath ?? ConfigFile;
        if (!File.Exists(path))
        {
            return new AppConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json);
            if (loaded is null)
            {
                return new AppConfig();
            }

            if (Array.IndexOf(ReplayDurationOptions, loaded.ReplaySeconds) < 0)
            {
                loaded.ReplaySeconds = 30;
            }
            if (loaded.Fps is not (30 or 60))
            {
                loaded.Fps = 30;
            }
            return loaded;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save(string? customPath = null)
    {
        var path = customPath ?? ConfigFile;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "EkranKaydedici";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                         ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppValueName, $"\"{exePath}\" --background");
                }
            }
            else
            {
                if (key.GetValue(AppValueName) is not null)
                {
                    key.DeleteValue(AppValueName, throwOnMissingValue: false);
                }
            }
        }
        catch
        {
            // Ignore restricted registry environments gracefully.
        }
    }
}
