using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Radix.Editor;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Radix.Editor.Tests.TestAppBuilder))]

namespace Radix.Editor.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            })
            .UseSkia()
            .WithInterFont();
}
