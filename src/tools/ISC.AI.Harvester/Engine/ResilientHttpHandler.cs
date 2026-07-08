using System.Net;

namespace ISC.AI.Harvester.Engine;

/// <summary>
/// HTTP-обработчик устойчивости для массового сбора (Э4-17): выдерживает <b>минимальный интервал</b> между
/// запросами (вежливость к источнику) и <b>повторяет</b> запрос при 429/5xx с экспоненциальной задержкой
/// (учитывая заголовок <c>Retry-After</c>). Без него на десятках тысяч запросов источник отвечает 429 и сбор падает.
/// </summary>
public sealed class ResilientHttpHandler(TimeSpan minInterval, int maxRetries = 4) : DelegatingHandler
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(cancellationToken);

            // Отправляем КЛОН — оригинал остаётся пригодным для повтора (запрос нельзя отправить дважды).
            using var toSend = await CloneAsync(request, cancellationToken);
            var response = await base.SendAsync(toSend, cancellationToken);

            var retriable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retriable || attempt >= maxRetries)
            {
                return response;
            }

            var delay = RetryDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        if (minInterval <= TimeSpan.Zero)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wait = minInterval - (DateTimeOffset.UtcNow - _lastSent);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            _lastSent = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date && date > DateTimeOffset.UtcNow)
        {
            return date - DateTimeOffset.UtcNow;
        }

        // Экспоненциально: 1, 2, 4, 8… с потолком 30 с.
        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };

        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }
}
