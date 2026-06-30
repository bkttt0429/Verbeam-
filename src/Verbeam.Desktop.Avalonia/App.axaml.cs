using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Linq;

namespace Verbeam.Desktop.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            desktop.MainWindow = new MainWindow(PickStartupWorkspace(args), PickSettingsSection(args));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string PickStartupWorkspace(string[] args)
    {
        var value = args
            .Select(arg => arg.StartsWith("--workspace=", StringComparison.OrdinalIgnoreCase)
                ? arg["--workspace=".Length..]
                : string.Empty)
            .FirstOrDefault(arg => !string.IsNullOrWhiteSpace(arg));

        return string.IsNullOrWhiteSpace(value)
            ? "translate"
            : value.Trim();
    }

    private static string PickSettingsSection(string[] args)
    {
        var value = args
            .Select(arg => arg.StartsWith("--settings-section=", StringComparison.OrdinalIgnoreCase)
                ? arg["--settings-section=".Length..]
                : string.Empty)
            .FirstOrDefault(arg => !string.IsNullOrWhiteSpace(arg));

        return string.IsNullOrWhiteSpace(value)
            ? "general"
            : value.Trim();
    }
}
