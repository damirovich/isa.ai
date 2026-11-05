using System.Globalization;
using System.Security.Claims;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Web.Security;

/// <summary>
/// Вход в систему (Э3-08, ТБ-010): проверка учётных данных во внешней системе идентификации,
/// сопоставление с локальным субъектом (<c>core.app_user</c>) и сборка принципала сессии.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТД-004): вход — аудируемое событие (ТБ-030), запись в журнал fail-closed: не записался
/// аудит — входа нет. Причины отказа наружу не различаются (единый «неверные данные»). Допуск при входе
/// НЕ выдаётся и не проверяется: локальная учётка создаётся БЕЗ допуска (default-deny, ТБ-012) — допуск
/// назначает администратор отдельно, и до этого retrieval невозможен.
///
/// ПРИВЯЗКА К ЛОКАЛЬНОМУ СУБЪЕКТУ — СТРОГО ПО ExternalId, НИКОГДА по имени входа (ADR-0016). Внешняя
/// система (СКИД) может переиспользовать логин (увольнение → новый сотрудник с тем же login, ротация
/// логина, коллизия однофамильцев) — привязка «по совпадению UserName» отдала бы новому человеку допуск
/// прежнего владельца логина в обход default-deny (ТБ-012). Если ExternalId не найден, а UserName уже
/// занят другой локальной учёткой — вход отклоняется явным конфликтом; переподключение выполняет
/// администратор, а не логика входа.
///
/// БЛОКИРОВКА ПОСЛЕ НЕУДАЧНЫХ ВХОДОВ (ТБ-081, ADR-0065). Неудача считается по учётке с этим именем входа
/// (<see cref="LoginLockout"/>); на пороге (5 подряд) учётка блокируется до снятия Администратором. Заблокированная
/// не входит и с ВЕРНЫМ паролем, а отказ тот же, что при неверном: пароль проверяется в любом случае, поэтому ни текст,
/// ни время ответа не выдают подборщику, что он нашёл пароль. Успешный вход обнуляет счётчик («подряд»). Здесь — для
/// любого порта идентичности: локального и адаптера внешней системы.
/// </remarks>
public sealed class LoginService(
    IExternalIdentityProvider identityProvider,
    IDbContextFactory<CoreDbContext> contextFactory,
    IAuditWriter auditWriter,
    LoginLockout lockout,
    ILogger<LoginService> logger)
{
    /// <summary>Исход попытки в журнале: вход разрешён.</summary>
    public const string GrantedResult = "granted";

    /// <summary>Исход попытки в журнале: отказ (неверные данные, отключённая учётка, конфликт имени).</summary>
    public const string DeniedResult = "denied";

    /// <summary>Исход попытки в журнале: отказ потому, что учётка заблокирована после неудачных входов (ТБ-081).</summary>
    public const string LockedResult = "locked";

    /// <summary>
    /// Аутентифицирует пару логин/пароль. Возвращает принципала для cookie-сессии либо <c>null</c>
    /// при отказе (без различения причин наружу — кроме отдельно обрабатываемой недоступности внешней
    /// системы, см. <see cref="AuthEndpoints"/>). Успех и отказ фиксируются в журнале аудита.
    /// </summary>
    /// <param name="login">Имя входа.</param>
    /// <param name="password">Пароль.</param>
    /// <param name="sessionId">
    /// Номер сеанса, который хост откроет при успехе (ТБ-080, ADR-0066): попадает в запись успешного входа; при отказе
    /// не пишется — сеанс не открывается. <see langword="null"/> — запись входа без сеанса.
    /// </param>
    /// <param name="cancellationToken">Отмена.</param>
    public async Task<ClaimsPrincipal?> AuthenticateAsync(
        string login, string password, Guid? sessionId = null, CancellationToken cancellationToken = default)
    {
        var external = await identityProvider.VerifyCredentialsAsync(login, password, cancellationToken);
        if (external is null)
        {
            // ТБ-081: неудача — в счётчик учётки с этим именем входа; на пороге учётка блокируется, и запись о блокировке
            // ложится в журнал до её фиксации. Попытка на уже заблокированной учётке помечается в журнале «locked».
            var failure = await lockout.RegisterFailureAsync(login, cancellationToken);
            await WriteLoginAuditAsync(
                subjectId: null,
                login,
                failure.Outcome == FailedLoginOutcome.AlreadyLocked ? LockedResult : DeniedResult,
                cancellationToken);
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Привязка СТРОГО по внешнему идентификатору — см. remarks класса. UserName при этом никогда
        // не используется как ключ поиска: коллизия имени входа не должна перепривязывать субъекта.
        var user = await db.Users.FirstOrDefaultAsync(u => u.ExternalId == external.ExternalId, cancellationToken);
        if (user is null)
        {
            var userNameTaken = await db.Users.AnyAsync(u => u.UserName == external.Login, cancellationToken);
            if (userNameTaken)
            {
                // Имя входа уже занято другой локальной учёткой (переиспользованный логин во внешней
                // системе) — автопривязка небезопасна (унаследовала бы чужой допуск). Единый отказ +
                // аудит конфликта для разбора администратором.
                LoginServiceLog.UserNameConflict(logger, external.Login);
                await WriteLoginAuditAsync(subjectId: null, login, DeniedResult, cancellationToken);
                return null;
            }

            // Новая учётка — БЕЗ допуска: default-deny (ТБ-012), извлечение невозможно до назначения допуска.
            user = new AppUserEntity { UserName = external.Login, ExternalId = external.ExternalId };
            db.Users.Add(user);
        }

        user.DisplayName = external.DisplayName;

        // Локальная деактивация сильнее внешнего входа (ТБ-016): заблокированный у нас — не входит.
        if (!user.IsActive)
        {
            await db.SaveChangesAsync(cancellationToken);
            await WriteLoginAuditAsync(user.Id, login, DeniedResult, cancellationToken);
            return null;
        }

        // ТБ-081, ADR-0065: учётка заблокирована после неудачных входов подряд — не входит и с верным паролем; ответ тот же,
        // что при неверном (см. remarks). Снять блокировку может только Администратор.
        if (user.LockedOutAt is not null)
        {
            await db.SaveChangesAsync(cancellationToken);
            await WriteLoginAuditAsync(user.Id, login, LockedResult, cancellationToken);
            return null;
        }

        // «Подряд»: успешный вход обнуляет счётчик неудач.
        user.FailedLoginCount = 0;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Гонка при одновременном первом входе (два запроса с одним ещё не существующим ExternalId) —
            // проигравший запрос не падает наружу: перечитываем строку, которую вставил победитель.
            LoginServiceLog.ConcurrentJitInsertLost(logger, ex);
            db.ChangeTracker.Clear();
            user = await db.Users.SingleOrDefaultAsync(u => u.ExternalId == external.ExternalId, cancellationToken);
            if (user is null || !user.IsActive || user.LockedOutAt is not null)
            {
                await WriteLoginAuditAsync(
                    user?.Id, login, user?.LockedOutAt is not null ? LockedResult : DeniedResult, cancellationToken);
                return null;
            }
        }

        // Аудит успешного входа — ДО выдачи сессии (fail-closed: нет записи — нет входа). Запись уже несёт номер сеанса,
        // который хост откроет (ТБ-080).
        await WriteLoginAuditAsync(user.Id, login, GrantedResult, sessionId, cancellationToken);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, external.Login),
                new Claim(AuthClaims.ExternalId, external.ExternalId),
                new Claim(AuthClaims.SecurityStamp, external.SecurityStamp),
                new Claim(AuthClaims.DisplayName, external.DisplayName ?? external.Login),

                // Временный пароль (создание учётки, сброс администратором, учётка по умолчанию):
                // до смены оболочка не пускает никуда, кроме страницы смены пароля. Флаг берётся из
                // ЛОКАЛЬНОЙ учётки, а не из внешнего ответа: после §6.5 источник истины здесь.
                new Claim(
                    AuthClaims.MustChangePassword,
                    user.MustChangePassword ? "1" : "0"),
                new Claim(AuthClaims.ValidatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return new ClaimsPrincipal(identity);
    }

    // Гриф записи — 0: событие входа не несёт содержимого документов, только метаданные (ТБ-032). Исход (ТБ-080): вход —
    // успех, любой отказ, в том числе по блокировке, — отказ; result= в тексте остаётся — по нему читаются старые записи.
    // Адрес — запроса входа: писатель журнала берёт его из контекста субъекта (ADR-0066).
    private Task WriteLoginAuditAsync(int? subjectId, string login, string result, CancellationToken cancellationToken) =>
        WriteLoginAuditAsync(subjectId, login, result, sessionId: null, cancellationToken);

    private Task WriteLoginAuditAsync(
        int? subjectId, string login, string result, Guid? sessionId, CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Login,
                Classification: 0,
                SubjectId: subjectId,
                ObjectRef: $"login={login};result={result}",
                Outcome: result == GrantedResult ? AuditOutcome.Success : AuditOutcome.Denied,
                SessionId: sessionId),
            cancellationToken);
}
