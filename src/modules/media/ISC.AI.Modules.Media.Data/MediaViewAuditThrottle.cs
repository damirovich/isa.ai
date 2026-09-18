using System.Collections.Concurrent;
using System.Globalization;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Правило «одна выдача файла субъекту — одна запись аудита» (ТБ-030): просмотр носителя фиксируется на
/// ПЕРВОЕ обращение субъекта к файлу, а повторные обращения к тому же файлу в пределах окна (продолжения
/// <c>Range</c>-запросов при воспроизведении и перемотке видео, повторные загрузки вырезки на той же
/// странице) журнал не множат. Окно — скользящее по субъекту и объекту, а не по заголовку <c>Range</c>:
/// правило по заголовку обходилось бы первым же запросом <c>bytes=1-</c> без записи в журнал.
/// </summary>
/// <remarks>
/// Проверка допуска выполняется на КАЖДЫЙ запрос — правило касается только журнала. Состояние — в памяти
/// процесса (singleton): после рестарта первая выдача аудируется заново — это «лишняя» запись, а не
/// пропущенная (fail-closed для журнала). Память ограничена периодической чисткой устаревших ключей.
/// </remarks>
public sealed class MediaViewAuditThrottle(TimeProvider? timeProvider = null, TimeSpan? window = null)
{
    /// <summary>Окно по умолчанию: повторные обращения к тому же файлу в течение 5 минут — одна выдача.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAudited = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _window = window ?? DefaultWindow;

    /// <summary>
    /// Нужно ли писать запись <c>View</c> для обращения субъекта <paramref name="subjectId"/> к объекту
    /// <paramref name="objectRef"/> сейчас. Первое обращение — всегда «да»; повторное в окне — «нет».
    /// </summary>
    public bool ShouldAudit(int? subjectId, string objectRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectRef);

        var now = _time.GetUtcNow();
        var key = (subjectId?.ToString(CultureInfo.InvariantCulture) ?? "-") + "|" + objectRef;
        var audit = false;
        _lastAudited.AddOrUpdate(
            key,
            _ =>
            {
                audit = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last < _window)
                {
                    return last;
                }

                audit = true;
                return now;
            });

        if (_lastAudited.Count > PruneThreshold)
        {
            Prune(now);
        }

        return audit;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in _lastAudited)
        {
            if (now - entry.Value >= _window)
            {
                _lastAudited.TryRemove(entry.Key, out _);
            }
        }
    }
}
