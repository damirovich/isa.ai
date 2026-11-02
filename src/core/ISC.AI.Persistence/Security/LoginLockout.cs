using System.Globalization;
using ISC.AI.Abstractions.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Persistence.Security;

/// <summary>Итог учёта неудачного входа (ТБ-081, ADR-0065).</summary>
public enum FailedLoginOutcome
{
    /// <summary>Учётной записи с таким именем входа нет — считать нечего.</summary>
    UnknownAccount,

    /// <summary>Неудача учтена, порог ещё не достигнут.</summary>
    Counted,

    /// <summary>Эта неудача достигла порога — учётная запись заблокирована сейчас.</summary>
    LockedNow,

    /// <summary>Учётная запись уже заблокирована — счётчик больше не растёт.</summary>
    AlreadyLocked,
}

/// <summary>Итог учёта неудачного входа: что произошло, с какой учётной записью и сколько неудач подряд.</summary>
/// <param name="Outcome">Что произошло.</param>
/// <param name="UserId">Учётная запись; <see langword="null"/> — имени входа нет в реестре.</param>
/// <param name="FailedCount">Неудачных входов подряд после этой попытки.</param>
public sealed record FailedLoginResult(FailedLoginOutcome Outcome, int? UserId = null, int FailedCount = 0);

/// <summary>
/// Блокировка учётной записи после неудачных входов подряд (ТБ-081, ADR-0065): счётчик в <c>core.app_user</c>, на пороге —
/// отметка блокировки и запись в журнал аудита. Снимает блокировку только Администратор
/// (<see cref="UserAccountStore.UnlockAsync"/>).
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТД-004). (1) Учёт — одна транзакция под блокировкой строки учётки (<c>FOR UPDATE</c>): параллельные попытки
/// не теряют приращений, и учётка блокируется ровно один раз. (2) Запись журнала о блокировке — ДО фиксации (fail-closed,
/// как у уничтожения): журнал недоступен — исключение, блокировки нет. (3) Наружу причина отказа не уходит — вход отвечает
/// одинаково на неверный пароль и на заблокированную учётку. (4) Порог выключить нельзя: ноль, минус и мусор в настройке —
/// умолчание; требование безопасности не должно сниматься опечаткой.
/// </remarks>
public sealed class LoginLockout(
    IDbContextFactory<CoreDbContext> contextFactory,
    IAuditWriter auditWriter,
    int threshold = LoginLockout.DefaultThreshold)
{
    /// <summary>Ключ настройки: после скольких неудачных входов подряд учётная запись блокируется.</summary>
    public const string ThresholdKey = "Auth:LockoutAfterFailedLogins";

    /// <summary>Пять неудачных входов подряд (ТБ-081, параметр Заказчика).</summary>
    public const int DefaultThreshold = 5;

    /// <summary>Порог блокировки, не меньше одного.</summary>
    public int Threshold { get; } = threshold >= 1 ? threshold : DefaultThreshold;

    /// <summary>Порог из конфигурации (<see cref="ThresholdKey"/>); отсутствует, мусор, ноль или минус — <see cref="DefaultThreshold"/>.</summary>
    public static int ReadThreshold(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return int.TryParse(configuration[ThresholdKey], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 1
            ? value
            : DefaultThreshold;
    }

    /// <summary>Ссылка на объект в журнале — событие блокировки учётной записи (для истории сотрудника).</summary>
    public static string LockoutObjectRef(int userId) =>
        "auth:account:" + userId.ToString(CultureInfo.InvariantCulture) + ":lockout";

    /// <summary>
    /// Учитывает неудачный вход по имени входа (без хвостовых пробелов — та же нормализация, что у проверки пароля):
    /// счётчик +1, на пороге — блокировка с записью в журнал. Неизвестное имя входа ничего не меняет.
    /// </summary>
    public async Task<FailedLoginResult> RegisterFailureAsync(string login, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return new FailedLoginResult(FailedLoginOutcome.UnknownAccount);
        }

        var userName = login.Trim();

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Блокировка строки учётки ДО чтения счётчика: параллельная попытка ждёт и видит уже увеличенное значение.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM core.app_user WHERE user_name = {userName} FOR UPDATE", cancellationToken);

        // Глобальный фильтр мягкого удаления скрывает удалённые учётки — их и не считаем.
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken);
        if (user is null)
        {
            return new FailedLoginResult(FailedLoginOutcome.UnknownAccount);
        }

        if (user.LockedOutAt is not null)
        {
            return new FailedLoginResult(FailedLoginOutcome.AlreadyLocked, user.Id, user.FailedLoginCount);
        }

        user.FailedLoginCount++;
        var lockedNow = user.FailedLoginCount >= Threshold;
        if (lockedNow)
        {
            user.LockedOutAt = DateTime.UtcNow;

            // FAIL-CLOSED: запись о блокировке — до фиксации. Субъекта нет: блокирует система, а того, кто подбирал пароль,
            // журнал знает только по попыткам входа. Гриф 0 — метаданные учётки, содержимого документов нет (ТБ-032).
            var objectRef = LockoutObjectRef(user.Id);
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Modify,
                    Classification: 0,
                    SubjectId: null,
                    ObjectRef: objectRef,
                    PayloadSensitive: objectRef + ":attempts=" + user.FailedLoginCount.ToString(CultureInfo.InvariantCulture)),
                cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new FailedLoginResult(
            lockedNow ? FailedLoginOutcome.LockedNow : FailedLoginOutcome.Counted, user.Id, user.FailedLoginCount);
    }
}
