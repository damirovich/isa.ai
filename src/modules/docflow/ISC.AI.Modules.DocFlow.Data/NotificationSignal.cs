using System.Collections.Concurrent;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Реализация <see cref="INotificationSignal"/>: singleton-словарь «пользователь → живые подписки».
/// Данных не хранит и не переносит (инвариант — см. контракт); подписки живут вместе с circuit-ами
/// и снимаются их Dispose.
/// </summary>
public sealed class NotificationSignal : INotificationSignal
{
    // Внешний словарь по пользователю, внутренний — по подписке (у пользователя может быть
    // несколько открытых вкладок, у каждой — колокольчик и, возможно, лента).
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<Guid, Func<Task>>> _subscribers = new();

    /// <inheritdoc />
    public void Publish(IReadOnlyCollection<int> recipientUserIds)
    {
        ArgumentNullException.ThrowIfNull(recipientUserIds);

        foreach (var userId in recipientUserIds.Distinct())
        {
            if (!_subscribers.TryGetValue(userId, out var callbacks))
            {
                continue; // пользователь не в системе — уведомление дождётся его в БД
            }

            foreach (var callback in callbacks.Values)
            {
                // Fire-and-forget: писатель (команда или фоновая проверка сроков) не ждёт и не
                // зависит от чужих страниц; упавший колбэк (умерший circuit) глотается — это гонка
                // разрушения страницы с сигналом, а не ошибка писателя.
                _ = InvokeSafeAsync(callback);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(int userId, Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var key = Guid.NewGuid();
        _subscribers.GetOrAdd(userId, _ => new ConcurrentDictionary<Guid, Func<Task>>())[key] = callback;
        return new Subscription(this, userId, key);
    }

    private static async Task InvokeSafeAsync(Func<Task> callback)
    {
        try
        {
            await callback().ConfigureAwait(false);
        }
        catch
        {
            // Намеренно без лога: единственный ожидаемый сбой — разрушенный circuit подписчика.
        }
    }

    private void Remove(int userId, Guid key)
    {
        if (_subscribers.TryGetValue(userId, out var callbacks))
        {
            callbacks.TryRemove(key, out _);
        }
    }

    private sealed class Subscription(NotificationSignal owner, int userId, Guid key) : IDisposable
    {
        public void Dispose() => owner.Remove(userId, key);
    }
}
