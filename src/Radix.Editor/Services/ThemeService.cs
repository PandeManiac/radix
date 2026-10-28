using Avalonia;
using Avalonia.Styling;

namespace Radix.Editor.Services;

/// <summary>User preference for the editor colour scheme.</summary>
public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Applies the selected <see cref="ThemeMode"/> to the Avalonia application and
/// persists it under the platform application-data directory. Only presentation
/// state lives here; the simulation kernel is unaffected by themes.
/// </summary>
public static class ThemeService
{
    private static bool _initialized;
    private static string? _preferencePath;

    public static ThemeMode Current { get; private set; } = ThemeMode.System;

    public static event EventHandler? Changed;

    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>Loads and applies the saved preference once per process.</summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Current = ReadPreference();
        Apply(Current);
    }

    /// <summary>
    /// Changes the active theme. Persistence can be disabled by tests that only
    /// need an isolated render.
    /// </summary>
    public static void SetMode(ThemeMode mode, bool persist = true)
    {
        Current = mode;
        Apply(mode);
        if (persist)
        {
            SavePreference(mode);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static string ToStorageValue(ThemeMode mode) => mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        _ => "system",
    };

    public static ThemeMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    private static void Apply(ThemeMode mode)
    {
        if (Application.Current is not Application application)
        {
            return;
        }

        application.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private static ThemeMode ReadPreference()
    {
        try
        {
            string path = PreferencePath();
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : ThemeMode.System;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ThemeMode.System;
        }
    }

    private static void SavePreference(ThemeMode mode)
    {
        try
        {
            string path = PreferencePath();
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, ToStorageValue(mode));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A read-only home directory must not make the editor unusable.
        }
    }

    private static string PreferencePath()
    {
        _preferencePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Radix",
            "theme.txt");
        return _preferencePath;
    }
}
