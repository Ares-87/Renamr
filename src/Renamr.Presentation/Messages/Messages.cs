using Renamr.Core.Errors;

namespace Renamr.Presentation.Messages;

/// <summary>Un file ha prodotto un errore o un avviso: lo raccoglie il pannello Errori/Avvisi.</summary>
public sealed record FileIssueMessage(string FilePath, RenamrError Error, string Phase);

/// <summary>Inizia una nuova fase (analisi o esecuzione): il pannello si svuota.</summary>
public sealed record RunStartedMessage(string Phase);

/// <summary>Fine esecuzione, con riepilogo (usato per l'InfoBar e per eventuali notifiche).</summary>
public sealed record RunCompletedMessage(int Succeeded, int Failed, int Warnings, bool DryRun, string? JournalFile);
