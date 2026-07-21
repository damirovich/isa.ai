using ISC.AI.Abstractions.Security;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Identity.Skid;

/// <summary>
/// Провайдер идентичности поверх БД СКИД (Э3-08, ТБ-010/013): проверяет логин/пароль по
/// <c>public.users</c> и отдаёт состояние учётки для ревалидации сессий. Только чтение.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ БЕЗОПАСНОСТИ (ТД-003/ТД-004): причины отказа не различаются наружу — неизвестный логин,
/// неверный пароль и блокировка возвращают одинаковый <c>null</c>; для неизвестного логина выполняется
/// холостая Argon2-проверка (выравнивание времени). Допуск/гриф провайдер НЕ выдаёт — в СКИД этого
/// понятия нет; допуск ведётся локально в ядре (default-deny, ТБ-012).
/// </remarks>
public sealed class SkidExternalIdentityProvider(IDbContextFactory<SkidDbContext> contextFactory)
    : IExternalIdentityProvider
{
    /// <inheritdoc />
    public async Task<ExternalIdentity?> VerifyCredentialsAsync(
        string login, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        // СКИД сама триммит логин перед сверкой (LoginEndpoint) — повторяем, иначе пользователь с
        // хвостовым пробелом из буфера обмена входит в СКИД, но получает отказ в ISC.AI.
        login = login.Trim();

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Login == login, cancellationToken);
        if (user is null)
        {
            // Выравнивание времени: отказ «нет пользователя» не должен быть быстрее отказа «неверный пароль».
            SkidPasswordHash.VerifyDummy(password);
            return null;
        }

        if (!SkidPasswordHash.Verify(user.PasswordHash, password) || user.IsBlocked)
        {
            return null;
        }

        // Временный пароль СКИД (создание/сброс администратором) — единый отказ, как и для прочих
        // причин: смену пароля пользователь обязан выполнить в самой СКИД, ISC.AI не боковая дверь.
        if (user.MustChangePassword)
        {
            return null;
        }

        // ВНИМАНИЕ: DepartmentCode пока НИ НА ЧТО не влияет — сопоставление с подразделениями/допуском
        // (DivisionScope) выполняет администратор ISC.AI вручную (ADR-0016). Поле — задел на будущее
        // (напр. подсказка администратору при назначении допуска), не источник авторизации.
        string? departmentCode = null;
        if (user.DepartmentId is { } departmentId)
        {
            departmentCode = await db.Departments
                .Where(d => d.Id == departmentId)
                .Select(d => d.Code)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new ExternalIdentity(
            user.Id.ToString("D"),
            user.Login,
            user.FullName,
            departmentCode,
            user.SecurityStamp);
    }

    /// <inheritdoc />
    public async Task<ExternalIdentityState?> GetStateAsync(
        string externalId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(externalId, out var id))
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var user = await db.Users
            .Where(u => u.Id == id)
            .Select(u => new { u.IsBlocked, u.SecurityStamp })
            .FirstOrDefaultAsync(cancellationToken);

        return user is null ? null : new ExternalIdentityState(user.IsBlocked, user.SecurityStamp);
    }
}
