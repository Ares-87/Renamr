using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Renamr.Core.Abstractions;
using Renamr.Core.Localization;
using Renamr.Linux.Services;
using Renamr.Linux.Views;
using Renamr.Presentation.Services;
using Renamr.Presentation.ViewModels;
using Renamr.Services;
using Renamr.Services.Settings;

namespace Renamr.Linux;

/// <summary>Composition root: stesso grafo di Renamr.App, con i servizi di piattaforma per Linux.</summary>
public sealed partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services => ((App)Current!)._host!.Services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var services = builder.Services;
        // Al posto di DPAPI: AES-GCM con chiave privata dell'utente in ~/.local/share/Renamr.
        services.AddSingleton<ISecretProtector, FileKeySecretProtector>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddRenamrServices();

        // Presentazione
        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<WindowContext>();
        services.AddSingleton<IFolderPickerService, FolderPickerService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IssuesViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddSingleton<MainWindow>();
        _host = builder.Build();

        // Lingua dell'interfaccia: quella scelta nelle impostazioni, altrimenti quella del sistema (inglese se non c'è).
        var uiLanguage = _host.Services.GetRequiredService<InterfaceSettingsStore>().Load().Language;
        Strings.Current.SetLanguage(Strings.Resolve(uiLanguage, CultureInfo.CurrentUICulture));

        AddBrandResources();

        // Ultima rete di sicurezza: un'eccezione non gestita finisce nel log invece di chiudere l'app a metà lavoro.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            _host.Services.GetRequiredService<ILogger<App>>().LogCritical(e.Exception, "Eccezione non gestita");
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = _host.Services.GetRequiredService<MainWindow>();
            _host.Services.GetRequiredService<WindowContext>().Window = window;
            desktop.MainWindow = window;

            // "Renamr /percorso/libreria" apre subito la cartella (comodo da terminale o dal menu "Apri con").
            if (desktop.Args is [var folder, ..] && Directory.Exists(folder))
            {
                window.Opened += async (_, _) => await window.ViewModel.OpenFolderCommand.ExecuteAsync(folder);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Logo e scritta in due varianti: navy sul tema chiaro, bianco sul tema scuro (DynamicResource li scambia da solo).</summary>
    private void AddBrandResources()
    {
        static Bitmap Load(string file) => new(AssetLoader.Open(new Uri($"avares://Renamr/Assets/{file}")));

        Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
        {
            ["RenamrLogo"] = Load("logo-light.png"),
            ["RenamrWordmark"] = Load("wordmark-light.png"),
        };
        Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
        {
            ["RenamrLogo"] = Load("logo-dark.png"),
            ["RenamrWordmark"] = Load("wordmark-dark.png"),
        };
    }

    /// <summary>Dopo un cambio di lingua: stessa posizione e dimensione, stesso ViewModel, testi nuovi.</summary>
    public static void ReplaceMainWindow(MainWindow old)
    {
        ArgumentNullException.ThrowIfNull(old);
        var window = new MainWindow(old.ViewModel)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = old.Position,
            Width = old.Width,
            Height = old.Height,
            WindowState = old.WindowState,
        };
        Services.GetRequiredService<WindowContext>().Window = window;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = window;
        }
        window.Show();
        old.Close();
    }
}
