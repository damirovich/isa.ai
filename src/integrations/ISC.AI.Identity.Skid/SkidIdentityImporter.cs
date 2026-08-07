using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Identity.Skid;

/// <summary>Итог переноса учёток: сколько создано, обновлено и пропущено, и почему пропущено.</summary>
public sealed record IdentityImportResult(
    int Read, int Created, int PasswordsImported, int SkippedAlreadyLocal, int SkippedNoHash);

/// <summary>
/// Одноразовый перенос учёток из БД СКИД в <c>core.app_user</c> (Э4-35 §6.5, шаг 2): логин, ФИО,
/// хеш пароля, штамп безопасности, признак блокировки и признак временного пароля.
/// </summary>
/// <remarks>
/// ЧТЕНИЕ ЧУЖОЙ БД — ТОЛЬКО ЧЕРЕЗ <see cref="SkidDbContext"/> (он физически не умеет писать).
/// Пароли в открытом виде НЕ участвуют: переносится PHC-строка Argon2id, а формат у нас тот же —
/// поэтому люди входят своими прежними паролями, менять ничего не требуется (решение заказчика).
///
/// ИДЕМПОТЕНТНОСТЬ И ГЛАВНАЯ ЗАЩИТА: повторный запуск НЕ ПЕРЕЗАПИСЫВАЕТ уже перенесённый пароль.
/// Хеш ставится только там, где локального пароля ещё нет (<c>PasswordHash is null</c>). Без этого
/// правила второй прогон после перехода откатил бы пароли всем, кто уже сменил их у нас, — причём
/// молча и необратимо (старый хеш из СКИД снова стал бы действующим).
///
/// Сопоставление — по <see cref="AppUserEntity.ExternalId"/> (идентификатор учётки СКИД). Логин при
/// повторном переносе НЕ меняется: он у нас уникален и мог быть исправлен администратором вручную.
/// </remarks>
public sealed class SkidIdentityImporter(
    IDbContextFactory<SkidDbContext> skidContextFactory,
    IDbContextFactory<CoreDbContext> coreContextFactory)
{
    /// <summary>Переносит учётки. Ничего не удаляет: лишние локальные учётки остаются как были.</summary>
    public async Task<IdentityImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        await using var skid = await skidContextFactory.CreateDbContextAsync(cancellationToken);
        await using var core = await coreContextFactory.CreateDbContextAsync(cancellationToken);

        var skidUsers = await skid.Users
            .Select(u => new
            {
                u.Id, u.Login, u.PasswordHash, u.FullName, u.IsBlocked, u.SecurityStamp, u.MustChangePassword,
            })
            .ToListAsync(cancellationToken);

        // Локальные учётки читаются ОДНИМ запросом: перенос идёт на пустой системе или близко к ней,
        // а построчные обращения к БД на каждого пользователя ради удобства кода того не стоят.
        var existing = await core.Users
            .Where(u => u.ExternalId != null)
            .ToDictionaryAsync(u => u.ExternalId!, cancellationToken);

        var created = 0;
        var passwordsImported = 0;
        var skippedAlreadyLocal = 0;
        var skippedNoHash = 0;

        foreach (var source in skidUsers)
        {
            var externalId = source.Id.ToString("D");

            if (!existing.TryGetValue(externalId, out var user))
            {
                user = new AppUserEntity
                {
                    UserName = source.Login.Trim(),
                    ExternalId = externalId,
                };
                core.Users.Add(user);
                created++;
            }

            // ФИО и блокировка — переносятся всегда: это состояние учётки, а не секрет.
            user.DisplayName = string.IsNullOrWhiteSpace(source.FullName) ? null : source.FullName.Trim();
            user.IsActive = !source.IsBlocked;

            if (string.IsNullOrWhiteSpace(source.PasswordHash))
            {
                // Учётка СКИД без хеша (битая строка) — переносим состояние, но войти по ней нельзя.
                skippedNoHash++;
                continue;
            }

            if (user.PasswordHash is not null)
            {
                // Пароль уже локальный — НЕ трогаем (мог быть сменён после перехода).
                skippedAlreadyLocal++;
                continue;
            }

            user.PasswordHash = source.PasswordHash;
            user.SecurityStamp = source.SecurityStamp;

            // Временный пароль СКИД известен третьему лицу (администратору, выдавшему сброс).
            // Переносим признак: вход по такому паролю обязан требовать смены (шаг 3 §6.5).
            user.MustChangePassword = source.MustChangePassword;
            passwordsImported++;
        }

        await core.SaveChangesAsync(cancellationToken);

        return new IdentityImportResult(
            skidUsers.Count, created, passwordsImported, skippedAlreadyLocal, skippedNoHash);
    }
}
