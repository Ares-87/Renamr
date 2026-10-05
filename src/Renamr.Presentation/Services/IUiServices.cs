namespace Renamr.Presentation.Services;

/// <summary>Selettori di cartella e di file nativi (implementati nella View, serve l'handle della finestra).</summary>
public interface IFolderPickerService
{
    Task<string?> PickFolderAsync();

    /// <summary>Uno o più file. <paramref name="extensions"/> (".mkv", ".mp3"…) limita la scelta; null = qualunque file.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(IReadOnlyCollection<string>? extensions) => Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Apertura di Esplora File sul file indicato (comodo dal pannello errori) e dei link nel browser.</summary>
public interface IShellService
{
    void RevealInExplorer(string path);

    /// <summary>Apre l'indirizzo nel browser predefinito.</summary>
    void OpenUrl(string url);
}

/// <summary>Link fissi dell'app.</summary>
public static class AppLinks
{
    /// <summary>Modulo di donazione PayPal dello sviluppatore ("offri un caffè").</summary>
    public const string Donate = "https://www.paypal.com/donate/?hosted_button_id=VU7PLDUSY9BHQ";
}
