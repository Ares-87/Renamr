using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Renamr.App.Services;
using Renamr.Core.Abstractions;
using Renamr.Core.Localization;
using Renamr.Presentation.Services;
using Renamr.Presentation.ViewModels;
using Renamr.Services;
using Renamr.Services.Settings;

namespace Renamr.App;

/// <summary>Composition root: tutto il grafo di dipendenze si costruisce qui, una volta.</summary>
public partial class App : Application
{
    private readonly IHost _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var services = builder.Services;
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
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

        // Ultima rete di sicurezza: un'eccezione non gestita finisce nel log invece di chiudere l'app a metà lavoro.
        UnhandledException += (_, e) =>
        {
            _host.Services.GetRequiredService<ILogger<App>>().LogCritical(e.Exception, "Eccezione non gestita");
            e.Handled = true;
        };
    }

    public static IServiceProvider Services => ((App)Current)._host.Services;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Lingua dell'interfaccia: quella scelta nelle impostazioni, altrimenti quella di Windows (inglese se non c'è).
        var uiLanguage = _host.Services.GetRequiredService<InterfaceSettingsStore>().Load().Language;
        Strings.Current.SetLanguage(Strings.Resolve(uiLanguage, CultureInfo.CurrentUICulture));

        _window = _host.Services.GetRequiredService<MainWindow>();
        _host.Services.GetRequiredService<WindowContext>().Window = _window;
        _window.Activate();
    }

    /// <summary>Dopo un cambio di lingua: stessa posizione e dimensione, stesso ViewModel, testi nuovi.</summary>
    public void ReplaceMainWindow(MainWindow old)
    {
        ArgumentNullException.ThrowIfNull(old);
        var window = new MainWindow(old.ViewModel);
        window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            old.AppWindow.Position.X, old.AppWindow.Position.Y, old.AppWindow.Size.Width, old.AppWindow.Size.Height));
        _window = window;
        _host.Services.GetRequiredService<WindowContext>().Window = window;
        window.Activate();
        old.StopTracking();
        old.Close();
    }
}
