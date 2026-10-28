using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Radix.Editor.Services;
using Xunit;

namespace Radix.Editor.Tests;

public sealed class ThemeServiceTests
{
    [Theory]
    [InlineData("light", ThemeMode.Light)]
    [InlineData("DARK", ThemeMode.Dark)]
    [InlineData(" system ", ThemeMode.System)]
    [InlineData("", ThemeMode.System)]
    [InlineData("sepia", ThemeMode.System)]
    public void StoredPreferenceParsesConservatively(string value, ThemeMode expected)
    {
        Assert.Equal(expected, ThemeService.Parse(value));
    }

    [Fact]
    public void PreferenceValuesRoundTrip()
    {
        foreach (ThemeMode mode in Enum.GetValues<ThemeMode>())
        {
            Assert.Equal(mode, ThemeService.Parse(ThemeService.ToStorageValue(mode)));
        }
    }

    [AvaloniaFact]
    public void SetModeUpdatesTheApplicationVariantWithoutPersisting()
    {
        Application application = Application.Current ?? throw new InvalidOperationException("No Avalonia application.");
        ThemeVariant? previous = application.RequestedThemeVariant;
        try
        {
            ThemeService.SetMode(ThemeMode.Dark, persist: false);
            Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);

            ThemeService.SetMode(ThemeMode.Light, persist: false);
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
        }
        finally
        {
            ThemeService.SetMode(ThemeMode.System, persist: false);
            application.RequestedThemeVariant = previous;
        }
    }
}
