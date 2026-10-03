using Renamr.Core.Options;

namespace Renamr.Core.Abstractions;

/// <summary>Accesso alle impostazioni correnti: i provider le rileggono a ogni chiamata (chiavi API modificabili a caldo).</summary>
public interface ISettingsStore
{
    RenamrSettings Current { get; }
    Task SaveAsync(RenamrSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Implementazione in memoria (test, strumenti a riga di comando).</summary>
public sealed class InMemorySettingsStore(RenamrSettings? settings = null) : ISettingsStore
{
    public RenamrSettings Current { get; private set; } = settings ?? new RenamrSettings();

    public Task SaveAsync(RenamrSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}
