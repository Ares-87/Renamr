using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Renamr.Core.Localization;
using Renamr.Presentation.Services;

namespace Renamr.Linux.Services;

/// <summary>Riferimento alla finestra principale (serve al selettore cartelle e agli appunti).</summary>
public sealed class WindowContext
{
    public Window? Window { get; set; }
}

/// <summary>Selettore cartella del desktop (GTK/KDE tramite il portale XDG quando c'è).</summary>
public sealed class FolderPickerService(WindowContext context) : IFolderPickerService
{
    public async Task<string?> PickFolderAsync()
    {
        var window = context.Window ?? throw new InvalidOperationException("Finestra non pronta");
        var storage = window.StorageProvider;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Current.PickFolderTitle,
            AllowMultiple = false,
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Videos),
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync(IReadOnlyCollection<string>? extensions)
    {
        var window = context.Window ?? throw new InvalidOperationException("Finestra non pronta");
        var storage = window.StorageProvider;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Current.PickFilesTitle,
            AllowMultiple = true,
            SuggestedStartLocation = await storage.TryGetWellKnownFolderAsync(WellKnownFolder.Videos),
            // Film e serie: solo i tipi che Renamr riconosce (maiuscole e minuscole, Linux le distingue).
            FileTypeFilter = extensions is null
                ? null
                : [new FilePickerFileType(Strings.Current.ModeMedia)
                {
                    Patterns = [.. extensions.SelectMany(e => new[] { "*" + e.ToLowerInvariant(), "*" + e.ToUpperInvariant() }).Distinct()],
                }],
        });
        return [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }
}

/// <summary>
/// "Mostra nella cartella": chiede al file manager (Nautilus, Dolphin, Nemo…) di evidenziare il file tramite
/// l'interfaccia D-Bus standard org.freedesktop.FileManager1; se non risponde apre la cartella con xdg-open.
/// </summary>
public sealed class ShellService : IShellService
{
    public void RevealInExplorer(string path)
    {
        if (File.Exists(path) && TryRun("dbus-send",
                "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                "org.freedesktop.FileManager1.ShowItems", $"array:string:{new Uri(path).AbsoluteUri}", "string:"))
        {
            return;
        }
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            TryRun("xdg-open", folder);
        }
    }

    public void OpenUrl(string url)
    {
        // Senza aspettare: xdg-open può restare attivo finché il browser è aperto.
        try
        {
            using var process = Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Nessun xdg-open: non c'è altro modo affidabile di aprire il browser.
        }
    }

    private static bool TryRun(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }
            if (!process.WaitForExit(3000))
            {
                // xdg-open può restare attivo finché il file manager è aperto: va bene così.
                return true;
            }
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
