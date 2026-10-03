namespace Renamr.Services.Resilience;

/// <summary>
/// Quando l'utente preme Annulla non interrompiamo le richieste di rete già partite: le lasciamo finire
/// in background e ignoriamo la risposta. Interromperle chiude il socket a metà, e su Windows .NET lancia
/// internamente una SocketException ("Operazione di I/O terminata a causa dell'uscita dal thread...") che
/// Visual Studio mostra fermando il debug, anche se l'app la gestisce già. Così Annulla resta immediato
/// e nessun socket viene chiuso a forza; il timeout di sicurezza impedisce che una richiesta resti appesa.
/// </summary>
public static class AbandonOnCancel
{
    /// <summary>Tetto per una richiesta abbandonata: oltre, la chiudiamo comunque.</summary>
    public static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Avvia <paramref name="call"/> con un token che scade solo dopo <paramref name="safetyTimeout"/>, e smette
    /// di aspettarla appena <paramref name="cancellationToken"/> viene annullato.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken, TimeSpan? safetyTimeout = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        // Niente Dispose: il CTS deve sopravvivere alla richiesta abbandonata; il suo timer si libera da solo alla scadenza.
#pragma warning disable CA2000
        var safety = new CancellationTokenSource(safetyTimeout ?? SafetyTimeout);
#pragma warning restore CA2000
        var work = call(safety.Token);
        var waited = work.WaitAsync(cancellationToken);
        _ = waited.ContinueWith(
            _ => work.ContinueWith(Observe, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default),
            CancellationToken.None, TaskContinuationOptions.OnlyOnCanceled | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return waited;
    }

    /// <summary>Una risposta arrivata dopo l'annullamento va liberata; un errore va solo osservato, non è più di nessuno.</summary>
    private static void Observe<T>(Task<T> abandoned)
    {
        if (abandoned.IsCompletedSuccessfully)
        {
            (abandoned.Result as IDisposable)?.Dispose();
        }
        else
        {
            _ = abandoned.Exception;
        }
    }
}

/// <summary>
/// Stessa logica per i client creati da IHttpClientFactory: è l'handler più esterno, quindi i timeout e i retry
/// della pipeline standard continuano a valere dentro. Il corpo della risposta viene letto qui, così nemmeno
/// la lettura del contenuto può essere interrotta a metà dall'annullamento.
/// </summary>
internal sealed class AbandonOnCancelHandler(TimeSpan safetyTimeout) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        AbandonOnCancel.RunAsync(ct => SendAndBufferAsync(request, ct), cancellationToken, safetyTimeout);

    private async Task<HttpResponseMessage> SendAndBufferAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        try
        {
            await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
