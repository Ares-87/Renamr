namespace Renamr.Presentation.Services;

/// <summary>Selettore cartella nativo (implementato nella View, serve l'handle della finestra).</summary>
public interface IFolderPickerService
{
    Task<string?> PickFolderAsync();
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
