using System.Globalization;
using System.Security.Claims;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
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
/// </remarks>
public sealed class LoginService(
    IExternalIdentityProvider identityProvider,
    IDbContextFactory<CoreDbContext> contextFactory,
    IAuditWriter auditWriter,
    ILogger<LoginService> logger)
{
    /// <summary>
    /// Аутентифицирует пару логин/пароль. Возвращает принципала для cookie-сессии либо <c>null</c>
    /// при отказе (без различения причин наружу — кроме отдельно обрабатываемой недоступности внешней
    /// системы, см. <see cref="AuthEndpoints"/>). Успех и отказ фиксируются в журнале аудита.
    /// </summary>
    public async Task<ClaimsPrincipal?> AuthenticateAsync(
        string login, string password, CancellationToken cancellationToken = default)
    {
        var external = await identityProvider.VerifyCredentialsAsync(login, password, cancellationToken);
        if (external is null)
        {
            await WriteLoginAuditAsync(subjectId: null, login, granted: false, cancellationToken);
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
                await WriteLoginAuditAsync(subjectId: null, login, granted: false, cancellationToken);
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
            await WriteLoginAuditAsync(user.Id, login, granted: false, cancellationToken);
            return null;
        }

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
            if (user is null || !user.IsActive)
            {
                await WriteLoginAuditAsync(user?.Id, login, granted: false, cancellationToken);
                return null;
            }
        }

        // Аудит успешного входа — ДО выдачи сессии (fail-closed: нет записи — нет входа).
        await WriteLoginAuditAsync(user.Id, login, granted: true, cancellationToken);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, external.Login),
                new Claim(AuthClaims.ExternalId, external.ExternalId),
                new Claim(AuthClaims.SecurityStamp, external.SecurityStamp),
                new Claim(AuthClaims.DisplayName, external.DisplayName ?? external.Login),
                new Claim(AuthClaims.ValidatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return new ClaimsPrincipal(identity);
    }

    // Гриф записи — 0: событие входа не несёт содержимого документов, только метаданные (ТБ-032).
    private Task WriteLoginAuditAsync(int? subjectId, string login, bool granted, CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Login,
                Classification: 0,
                SubjectId: subjectId,
                ObjectRef: $"login={login};result={(granted ? "granted" : "denied")}"),
            cancellationToken);
}
