using System.Globalization;
using System.Net;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Renamr.Core.Abstractions;
using Renamr.Core.Errors;

namespace Renamr.Services.Providers;

internal static class ProviderHelpers
{
    /// <summary>Converte le eccezioni di rete in <see cref="ProviderException"/>: il resolver passerà al provider successivo.</summary>
    public static ProviderException Wrap(string provider, Exception ex) => ex switch
    {
        ProviderException pe => pe,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
            new ProviderException(provider, RenamrErrorCode.ProviderAuthFailed, ex.Message, ex),
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            new ProviderException(provider, RenamrErrorCode.ProviderRateLimited, ex.Message, ex),
        UnauthorizedAccessException =>
            new ProviderException(provider, RenamrErrorCode.ProviderAuthFailed, ex.Message, ex),
        BrokenCircuitException or TimeoutRejectedException or HttpRequestException or TaskCanceledException =>
            new ProviderException(provider, RenamrErrorCode.ProviderUnavailable, ex.Message, ex),
        _ => new ProviderException(provider, RenamrErrorCode.ProviderUnavailable, ex.Message, ex),
    };

    /// <summary>Stabilisce se un'eccezione va convertita (tutto tranne l'annullamento voluto dall'utente).</summary>
    public static bool ShouldWrap(Exception ex, CancellationToken ct) =>
        !(ex is OperationCanceledException && ct.IsCancellationRequested);

    public static DateOnly? ParseDate(string? value, params string[] formats)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string[] all = [.. formats, "yyyy-MM-dd", "yyyy-MM", "yyyy", "d MMM yyyy", "dd MMM yyyy"];
        if (DateTime.TryParseExact(value.Trim(), all, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            return DateOnly.FromDateTime(dt);
        }
        return null;
    }

    public static double Rank(int index, int count) => count <= 1 ? 1 : 1.0 - (double)index / (count - 1);
}
