using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Renamr.Presentation.Services;
using Renamr.Services.Settings;
using Windows.Storage.Pickers;

namespace Renamr.App.Services;

/// <summary>Riferimento alla finestra principale (serve l'HWND ai picker WinRT nelle app desktop).</summary>
public sealed class WindowContext
{
    public Window? Window { get; set; }
}

public sealed class FolderPickerService(WindowContext context) : IFolderPickerService
{
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");

        // In un'app WinUI 3 desktop il picker WinRT va associato esplicitamente alla finestra.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(context.Window ?? throw new InvalidOperationException("Finestra non pronta"));
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}

public sealed class ShellService : IShellService
{
    public void RevealInExplorer(string path)
    {
        var target = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
    }

    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

/// <summary>DPAPI legata all'utente Windows corrente: le chiavi API sono leggibili solo da questo account.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Renamr.ProviderKeys.v1"u8.ToArray();

    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedValue) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
}
