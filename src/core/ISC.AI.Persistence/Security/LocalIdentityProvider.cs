using ISC.AI.Abstractions.Security;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Persistence.Security;

/// <summary>
/// ЛОКАЛЬНАЯ идентичность (Э4-35 §6.5): проверка логина и пароля по <c>core.app_user</c> вместо чужой
/// БД СКИД. Реализация того же порта <see cref="IExternalIdentityProvider"/> — меняется не порт,
/// а то, ЧТО за ним стоит; вход, cookie, штамп и ревалидация сессий не переписываются.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТД-003/ТД-004), те же, что у адаптера СКИД: причины отказа наружу НЕ различаются —
/// неизвестный логин, неверный пароль, блокировка и отсутствие локального пароля дают одинаковый
/// <see langword="null"/>; для неизвестного логина выполняется холостая Argon2-проверка (выравнивание
/// времени); пароль не логируется (ТБ-043). Допуск провайдер НЕ выдаёт — он ведётся отдельно
/// в <c>core.clearance</c> и по принципу default-deny из учётки не выводится (ТБ-012/021).
///
/// ОТЛИЧИЕ ОТ АДАПТЕРА СКИД: временный пароль (<c>MustChangePassword</c>) здесь НЕ отклоняет вход.
/// В СКИД отказ был осмыслен — пароль меняли в самой СКИД, и ISC.AI не был боковой дверью. После
/// перехода менять пароль негде, кроме как здесь: отказ запер бы человека навсегда. Вход разрешается,
/// а требование сменить пароль обеспечивает слой входа (шаг 3 §6.5).
/// </remarks>
public sealed class LocalIdentityProvider(IDbContextFactory<CoreDbContext> contextFactory)
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

        // Хвостовые пробелы (вставка из буфера) — та же нормализация, что была у адаптера СКИД.
        login = login.Trim();

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Глобальный фильтр мягкого удаления уже скрывает удалённые учётки.
        var user = await db.Users.AsNoTracking()
            .Where(u => u.UserName == login)
            .Select(u => new
            {
                u.Id, u.UserName, u.DisplayName, u.IsActive, u.PasswordHash, u.SecurityStamp, u.ExternalId,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            // Отказ «нет пользователя» не должен быть быстрее отказа «неверный пароль».
            PasswordHashing.VerifyDummy(password);
            return null;
        }

        if (!PasswordHashing.Verify(user.PasswordHash, password) || !user.IsActive)
        {
            return null;
        }

        // Штамп обязателен для ревалидации сессии: учётка без него ещё не переведена на локальный
        // вход (не импортирована) — это отказ, а не «пустой штамп», иначе сессию нечем инвалидировать.
        if (string.IsNullOrEmpty(user.SecurityStamp))
        {
            return null;
        }

        return new ExternalIdentity(
            // КРИТИЧНО: отдаётся СОХРАНЁННЫЙ ExternalId учётки, а не наш внутренний Id. Вход
            // (LoginService) связывает сессию с локальным субъектом СТРОГО по этому значению и
            // НИКОГДА по имени входа (ADR-0016). У перенесённых из СКИД учёток здесь лежит
            // идентификатор СКИД; верни мы свой номер — совпадения бы не нашлось, а имя входа уже
            // занято, и вход отказал бы ВСЕМ перенесённым сразу после переключения.
            //
            // Запасное значение — для учёток без внешней привязки. Экран создания учётки (шаг 3
            // §6.5) ОБЯЗАН проставлять ExternalId при создании, иначе такой пользователь упрётся
            // в тот же конфликт имени.
            user.ExternalId ?? $"local:{user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            user.UserName,
            user.DisplayName,
            // Подразделение из учётки НЕ выдаётся: связь человека с подразделениями выражена допуском
            // (core.clearance), и выводить её из учётки нельзя (default-deny, ТБ-012).
            DepartmentCode: null,
            user.SecurityStamp);
    }

    /// <inheritdoc />
    public async Task<ExternalIdentityState?> GetStateAsync(
        string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId))
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Ищем по тому же значению, которое выдали при входе (см. VerifyCredentialsAsync): у
        // перенесённых учёток это идентификатор СКИД, у локальных — «local:{id}».
        var user = await db.Users.AsNoTracking()
            .Where(u => u.ExternalId == externalId)
            .Select(u => new { u.IsActive, u.SecurityStamp })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null && externalId.StartsWith("local:", StringComparison.Ordinal)
            && int.TryParse(externalId.AsSpan("local:".Length), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var localId))
        {
            user = await db.Users.AsNoTracking()
                .Where(u => u.Id == localId)
                .Select(u => new { u.IsActive, u.SecurityStamp })
                .FirstOrDefaultAsync(cancellationToken);
        }

        // «Заблокирован» у нас выражается отключением учётки (IsActive) — отдельного флага нет.
        return user is null
            ? null
            : new ExternalIdentityState(!user.IsActive, user.SecurityStamp ?? string.Empty);
    }
}
