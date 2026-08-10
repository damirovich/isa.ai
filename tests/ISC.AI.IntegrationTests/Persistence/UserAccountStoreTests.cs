using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Security;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Ведение учётных записей и смена пароля (Э4-35 §6.5, шаг 3) на реальном PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Главное, что здесь закреплено, — РОТАЦИЯ ШТАМПА безопасности. Смена пароля, сброс и отключение
/// обязаны менять штамп: он и обрывает живые сессии (ТБ-016). Без ротации отобранный доступ
/// продолжал бы работать до истечения cookie — то есть ровно в тот промежуток, ради которого доступ
/// и отбирают.
/// </remarks>
public sealed class UserAccountStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Учётки §6.5: создание с временным паролем, вход по нему, смена пароля обрывает сессии")]
    public async Task Account_lifecycle_rotates_security_stamp()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var accounts = new UserAccountStore(factory);
        var identity = new LocalIdentityProvider(factory);

        // Пароль порождает вызывающий (сценарий), хранилище принимает уже готовый — в базу уходит хеш.
        const string temporary = "Vremenny1Parol";
        var userId = await accounts.CreateAsync("ivanov", "Иванов И.И.", temporary);
        userId.ShouldNotBeNull();

        // Имя входа уникально: повтор отклоняется, а не создаёт вторую учётку.
        (await accounts.CreateAsync("ivanov", "Двойник", temporary)).ShouldBeNull();

        var row = (await accounts.ListAsync()).Single(a => a.UserName == "ivanov");
        row.HasLocalPassword.ShouldBeTrue();
        row.MustChangePassword.ShouldBeTrue(); // временный пароль знает выдавший его администратор
        row.IsActive.ShouldBeTrue();

        // Созданной учёткой можно войти сразу — иначе временный пароль был бы бесполезен.
        var afterCreate = await identity.VerifyCredentialsAsync("ivanov", temporary);
        afterCreate.ShouldNotBeNull();
        var stampAfterCreate = afterCreate.SecurityStamp;

        // ExternalId проставлен при создании: вход связывает сессию с субъектом строго по нему,
        // и без него локальная учётка упёрлась бы в конфликт имени (ADR-0016).
        afterCreate.ExternalId.ShouldBe($"local:{userId.Value}");

        // Смена своего пароля: текущий обязателен, повтор старого отклоняется.
        (await accounts.ChangeOwnPasswordAsync(userId.Value, "не-тот", "НовыйПароль123"))
            .ShouldBe(PasswordChangeStatus.WrongCurrentPassword);
        (await accounts.ChangeOwnPasswordAsync(userId.Value, temporary, temporary))
            .ShouldBe(PasswordChangeStatus.SameAsCurrent);

        (await accounts.ChangeOwnPasswordAsync(userId.Value, temporary, "НовыйПароль123"))
            .ShouldBe(PasswordChangeStatus.Ok);

        // Новый пароль работает, старый — нет.
        var afterChange = await identity.VerifyCredentialsAsync("ivanov", "НовыйПароль123");
        afterChange.ShouldNotBeNull();
        (await identity.VerifyCredentialsAsync("ivanov", temporary)).ShouldBeNull();

        // ШТАМП СМЕНИЛСЯ — живые сессии завершатся ревалидацией (ТБ-016).
        afterChange.SecurityStamp.ShouldNotBe(stampAfterCreate);

        // Требование сменить пароль снято — человек больше не в переходном состоянии.
        (await accounts.ListAsync()).Single(a => a.UserName == "ivanov").MustChangePassword.ShouldBeFalse();

        // Сброс администратором: снова временный пароль, снова новый штамп (сессии обрываются).
        const string reissued = "SbroshennyParol1";
        (await accounts.ResetPasswordAsync(userId.Value, reissued)).ShouldBeTrue();

        var afterReset = await identity.VerifyCredentialsAsync("ivanov", reissued);
        afterReset.ShouldNotBeNull();
        afterReset.SecurityStamp.ShouldNotBe(afterChange.SecurityStamp);
        (await accounts.ListAsync()).Single(a => a.UserName == "ivanov").MustChangePassword.ShouldBeTrue();

        // Отключение: вход закрыт, штамп снова сменился — сессия не доживает до истечения cookie.
        (await accounts.SetActiveAsync(userId.Value, isActive: false)).ShouldBeTrue();
        (await identity.VerifyCredentialsAsync("ivanov", reissued)).ShouldBeNull();

        var state = await identity.GetStateAsync($"local:{userId.Value}");
        state.ShouldNotBeNull();
        state.IsBlocked.ShouldBeTrue();
        state.SecurityStamp.ShouldNotBe(afterReset.SecurityStamp);

        // Отключённый не может сменить пароль (иначе вернул бы себе доступ в обход администратора).
        (await accounts.ChangeOwnPasswordAsync(userId.Value, reissued, "ЕщёОдинПароль1"))
            .ShouldBe(PasswordChangeStatus.NotFound);

        // Включение обратно — вход снова работает прежним временным паролем.
        (await accounts.SetActiveAsync(userId.Value, isActive: true)).ShouldBeTrue();
        (await identity.VerifyCredentialsAsync("ivanov", reissued)).ShouldNotBeNull();

        // Несуществующие — отказ, а не исключение.
        (await accounts.ResetPasswordAsync(999_999, reissued)).ShouldBeFalse();
        (await accounts.SetActiveAsync(999_999, isActive: true)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Учётки §6.5: без локального пароля менять нечего — вход ещё внешний")]
    public async Task Account_without_local_password_cannot_change_it()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        int userId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var user = new AppUserEntity { UserName = "external-only", ExternalId = "skid-guid" };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var accounts = new UserAccountStore(factory);

        (await accounts.ChangeOwnPasswordAsync(userId, "любой", "НовыйПароль123"))
            .ShouldBe(PasswordChangeStatus.NoLocalPassword);

        // В списке это видно явно: по такой учётке войти нельзя, пока пароль не задан.
        (await accounts.ListAsync()).Single(a => a.UserName == "external-only")
            .HasLocalPassword.ShouldBeFalse();
    }
}
