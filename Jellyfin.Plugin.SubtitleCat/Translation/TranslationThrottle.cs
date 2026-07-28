using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubtitleCat.Translation;

/// <summary>
/// Spaces out requests to a translation service.
/// </summary>
/// <remarks>
/// Kept separate from the throttle on subtitlecat requests on purpose. The two services
/// are unrelated, and a translation backend that starts refusing requests must not also
/// stall subtitle searching.
/// </remarks>
internal sealed class TranslationThrottle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastRequestUtc = DateTime.MinValue;

    /// <summary>Waits until the next request is allowed to go out.</summary>
    /// <param name="minimumInterval">The minimum gap between requests.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task WaitAsync(TimeSpan minimumInterval, CancellationToken cancellationToken)
    {
        if (minimumInterval <= TimeSpan.Zero)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var since = DateTime.UtcNow - _lastRequestUtc;
            if (since < minimumInterval)
            {
                await Task.Delay(minimumInterval - since, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
