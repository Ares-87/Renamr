using Renamr.Core.Errors;

namespace Renamr.Presentation.ViewModels;

/// <summary>Voce del pannello Errori/Avvisi: codice leggibile + file + dettaglio tecnico.</summary>
public sealed record IssueItemViewModel(string FilePath, RenamrError Error, string Phase)
{
    public string FileName => Path.GetFileName(FilePath);
    public string Code => $"E{(int)Error.Code}";
    public string Message => Error.Message;

    /// <summary>Messaggio con il dettaglio tecnico, così la causa si legge senza passare il mouse.</summary>
    public string MessageWithDetail => string.IsNullOrWhiteSpace(Error.Detail) ? Error.Message : $"{Error.Message} ({Error.Detail})";
    public string? Detail => Error.Detail;
    public bool IsWarning => Error.IsWarning;
}
