namespace Renamr.Presentation.Services;

/// <summary>Selettore cartella nativo (implementato nella View, serve l'handle della finestra).</summary>
public interface IFolderPickerService
{
    Task<string?> PickFolderAsync();
}

/// <summary>Apertura di Esplora File sul file indicato (comodo dal pannello errori).</summary>
public interface IShellService
{
    void RevealInExplorer(string path);
}
