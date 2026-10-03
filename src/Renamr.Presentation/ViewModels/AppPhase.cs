namespace Renamr.Presentation.ViewModels;

/// <summary>Il flusso lineare a 3 fasi della finestra principale.</summary>
public enum AppPhase
{
    /// <summary>1. Drop-zone: nessuna cartella aperta.</summary>
    SelectFolder,

    /// <summary>Scansione e ricerca online in corso.</summary>
    Analyzing,

    /// <summary>2. Diff View: originale ➔ proposto.</summary>
    Preview,

    /// <summary>3. Esecuzione in corso (o simulazione).</summary>
    Running,

    /// <summary>Esecuzione terminata: riepilogo e possibilità di annullare.</summary>
    Completed,
}
