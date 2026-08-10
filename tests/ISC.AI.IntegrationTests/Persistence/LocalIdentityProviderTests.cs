using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Security;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Локальная идентичность (Э4-35 §6.5, шаг 1) на реальном PostgreSQL: вход по <c>core.app_user</c>
/// вместо чужой БД СКИД. Требуется Docker.
/// </summary>
/// <remarks>
/// Главное, что закрепляет тест, — ИНВАРИАНТ ТД-003: причины отказа наружу не различаются. Неизвестный
/// логин, неверный пароль, отключённая учётка и учётка без локального пароля дают ОДИН И ТОТ ЖЕ
/// <c>null</c>. Разошлись бы ответы — вход превратился бы в перечислитель существующих учёток.
/// </remarks>
public sealed class LocalIdentityProviderTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Локальный вход: верный пароль пускает, все виды отказа неотличимы друг от друга")]
    public async Task Local_login_verifies_password_and_hides_refusal_reasons()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());

        var stamp = PasswordHashing.NewSecurityStamp();
        int activeId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var active = new AppUserEntity
            {
                UserName = "ivanov",
                DisplayName = "Иванов И.И.",
                PasswordHash = PasswordHashing.Hash("верный-пароль"),
                SecurityStamp = stamp,
            };
            var disabled = new AppUserEntity
            {
                UserName = "petrov",
                PasswordHash = PasswordHashing.Hash("верный-пароль"),
                SecurityStamp = PasswordHashing.NewSecurityStamp(),
                IsActive = false,
            };

            // Учётка ещё НЕ переведена на локальный вход: пароля нет (переходный период §6.5).
            var notMigrated = new AppUserEntity { UserName = "sidorov" };

            db.Users.AddRange(active, disabled, notMigrated);
            await db.SaveChangesAsync();
            activeId = active.Id;
        }

        var provider = new LocalIdentityProvider(factory);

        // Успех: возвращается учётка со штампом — им ревалидируется сессия (ТБ-014/016).
        var identity = await provider.VerifyCredentialsAsync("ivanov", "верный-пароль");
        identity.ShouldNotBeNull();
        identity.Login.ShouldBe("ivanov");
        identity.DisplayName.ShouldBe("Иванов И.И.");
        identity.SecurityStamp.ShouldBe(stamp);

        // У учётки без внешней привязки идентификатор — «local:{id}». Возвращать голый номер нельзя:
        // вход ищет субъекта по ЭТОМУ значению, и у перенесённых из СКИД там лежит их идентификатор
        // (см. отдельный тест в SkidIdentityImporterTests).
        identity.ExternalId.ShouldBe(
            $"local:{activeId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        // Подразделение из учётки НЕ выдаётся: связь с подразделениями — это допуск (ТБ-012).
        identity.DepartmentCode.ShouldBeNull();

        // Хвостовые пробелы (вставка из буфера) — та же нормализация, что была у адаптера СКИД.
        (await provider.VerifyCredentialsAsync("  ivanov  ", "верный-пароль")).ShouldNotBeNull();

        // ЧЕТЫРЕ РАЗНЫЕ причины — один и тот же ответ (ТД-003).
        (await provider.VerifyCredentialsAsync("ivanov", "неверный")).ShouldBeNull();
        (await provider.VerifyCredentialsAsync("нет-такого", "любой")).ShouldBeNull();
        (await provider.VerifyCredentialsAsync("petrov", "верный-пароль")).ShouldBeNull();
        (await provider.VerifyCredentialsAsync("sidorov", "любой")).ShouldBeNull();

        // Пустые значения не проходят и не роняют провайдер.
        (await provider.VerifyCredentialsAsync("", "")).ShouldBeNull();
        (await provider.VerifyCredentialsAsync("ivanov", "")).ShouldBeNull();
    }

    [Fact(DisplayName = "Локальный вход: состояние учётки для ревалидации — отключение видно немедленно")]
    public async Task State_reflects_disabled_account_immediately()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());

        int userId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var user = new AppUserEntity
            {
                UserName = "state-check",
                PasswordHash = PasswordHashing.Hash("пароль"),
                SecurityStamp = PasswordHashing.NewSecurityStamp(),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var provider = new LocalIdentityProvider(factory);
        var externalId = $"local:{userId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        var state = await provider.GetStateAsync(externalId);
        state.ShouldNotBeNull();
        state.IsBlocked.ShouldBeFalse();

        // Отключение учётки = блокировка для ревалидации: живая сессия завершится (ТБ-016).
        await using (var db = factory.CreateDbContext())
        {
            var user = await db.Users.FirstAsync(u => u.Id == userId);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        (await provider.GetStateAsync(externalId)).ShouldNotBeNull().IsBlocked.ShouldBeTrue();

        // Несуществующий и нечисловой идентификатор — сессию завершить, а не упасть.
        (await provider.GetStateAsync("local:999999")).ShouldBeNull();
        (await provider.GetStateAsync("не-число")).ShouldBeNull();
        (await provider.GetStateAsync("")).ShouldBeNull();
    }

    [Fact(DisplayName = "Хеширование: формат PHC Argon2id, битый хеш не роняет вход")]
    public void Password_hashing_uses_phc_argon2id_and_survives_broken_input()
    {
        var hash = PasswordHashing.Hash("пароль");

        // Формат PHC libsodium — именно он совместим с хешами СКИД (перенос без смены паролей).
        hash.ShouldStartWith("$argon2id$");

        PasswordHashing.Verify(hash, "пароль").ShouldBeTrue();
        PasswordHashing.Verify(hash, "другой").ShouldBeFalse();

        // Битая строка — false, а не исключение: одна повреждённая запись не валит вход всей системы.
        PasswordHashing.Verify("не-хеш", "пароль").ShouldBeFalse();
        PasswordHashing.Verify(null, "пароль").ShouldBeFalse();
        PasswordHashing.Verify(hash, string.Empty).ShouldBeFalse();

        // Одинаковые пароли дают РАЗНЫЕ хеши (соль внутри PHC) — иначе хеши сравнивались бы напрямую.
        PasswordHashing.Hash("пароль").ShouldNotBe(hash);

        PasswordHashing.NewSecurityStamp().ShouldNotBe(PasswordHashing.NewSecurityStamp());
    }
}
