using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Persistence.Security;

/// <summary>
/// Ведение учётных записей поверх <c>core.app_user</c> (реализация <see cref="IUserAccountStore"/>).
/// Контекст — на операцию, через фабрику (ТС-008).
/// </summary>
public sealed class UserAccountStore(IDbContextFactory<CoreDbContext> contextFactory) : IUserAccountStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<UserAccountRow>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.Users.AsNoTracking()
            .Select(u => new
            {
                u.Id,
                u.UserName,
                u.DisplayName,
                u.IsActive,
                // НАРУЖУ УХОДИТ ТОЛЬКО ПРИЗНАК наличия пароля, не сам хеш: экрану достаточно знать,
                // можно ли этой учёткой войти, а хеш в разметке — лишняя поверхность утечки.
                HasPassword = u.PasswordHash != null,
                u.MustChangePassword,
            })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(u => u.DisplayName ?? u.UserName, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new UserAccountRow(
                u.Id, u.UserName, u.DisplayName, u.IsActive, u.HasPassword, u.MustChangePassword))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<int?> CreateAsync(
        string userName, string? displayName, string temporaryPassword,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPassword);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var login = userName.Trim();
        if (await db.Users.AnyAsync(u => u.UserName == login, cancellationToken))
        {
            return null;
        }

        var user = new AppUserEntity
        {
            UserName = login,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            PasswordHash = PasswordHashing.Hash(temporaryPassword),
            SecurityStamp = PasswordHashing.NewSecurityStamp(),

            // Пароль знает выдавший его администратор — до смены учётка неполноценна.
            MustChangePassword = true,
        };

        // ExternalId обязателен: вход связывает сессию с субъектом СТРОГО по нему (ADR-0016), и
        // локальная учётка без него упёрлась бы в конфликт имени — см. LocalIdentityProvider.
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        user.ExternalId = $"local:{user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        await db.SaveChangesAsync(cancellationToken);

        return user.Id;
    }

    /// <inheritdoc />
    public async Task<bool> ResetPasswordAsync(
        int userId, string temporaryPassword, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPassword);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = PasswordHashing.Hash(temporaryPassword);
        user.MustChangePassword = true;

        // Ротация штампа обрывает живые сессии владельца: сброс пароля обязан выкидывать того, кто
        // уже вошёл (иначе захваченная сессия переживает сброс — а его затем и делают).
        user.SecurityStamp = PasswordHashing.NewSecurityStamp();

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(
        int userId, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return false;
        }

        user.IsActive = isActive;

        if (!isActive)
        {
            // Отключение обязано действовать немедленно (ТБ-016), а не по истечении cookie.
            user.SecurityStamp = PasswordHashing.NewSecurityStamp();
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<PasswordChangeStatus> ChangeOwnPasswordAsync(
        int userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null)
        {
            return PasswordChangeStatus.NotFound;
        }

        if (user.PasswordHash is null)
        {
            return PasswordChangeStatus.NoLocalPassword;
        }

        // Знание ТЕКУЩЕГО пароля обязательно: владение сессией его не заменяет — иначе перехваченная
        // сессия позволила бы сменить пароль и закрепиться, вытеснив законного владельца.
        if (!PasswordHashing.Verify(user.PasswordHash, currentPassword))
        {
            return PasswordChangeStatus.WrongCurrentPassword;
        }

        if (PasswordHashing.Verify(user.PasswordHash, newPassword))
        {
            return PasswordChangeStatus.SameAsCurrent;
        }

        user.PasswordHash = PasswordHashing.Hash(newPassword);
        user.MustChangePassword = false;

        // Смена пароля завершает ВСЕ сессии, включая текущую: человек входит заново уже с новым
        // паролем. Так же ведёт себя и сама СКИД — штамп там меняется при смене пароля.
        user.SecurityStamp = PasswordHashing.NewSecurityStamp();

        await db.SaveChangesAsync(cancellationToken);
        return PasswordChangeStatus.Ok;
    }
}
