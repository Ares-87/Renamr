namespace Renamr.Services.Resilience;

/// <summary>
/// Intervallo minimo tra due richieste allo stesso servizio (AniDB: 1 ogni 2 s, MusicBrainz: 1 al secondo).
/// Superare questi limiti porta a un ban temporaneo dell'IP, quindi il throttle è globale per provider.
/// </summary>
public sealed class RequestThrottle(TimeSpan minInterval, TimeProvider? time = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last + minInterval - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            finally
            {
                _last = _time.GetUtcNow();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
