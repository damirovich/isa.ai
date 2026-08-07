using ISC.AI.Identity.Skid;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Перенос учёток из БД СКИД в <c>core.app_user</c> (Э4-35 §6.5, шаг 2) на реальном PostgreSQL:
/// таблица СКИД поднимается вручную (её схемой ISC.AI не владеет). Требуется Docker.
/// </summary>
/// <remarks>
/// Ключевая проверка — ПОВТОРНЫЙ ЗАПУСК не перезаписывает уже локальный пароль. Без этого правила
/// второй прогон после перехода молча откатил бы пароли всем, кто уже сменил их у нас: старый хеш
/// из СКИД снова стал бы действующим, а человек об этом даже не узнал бы.
/// </remarks>
public sealed class SkidIdentityImporterTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Перенос §6.5: учётки и хеши переносятся, повтор НЕ откатывает сменённый пароль")]
    public async Task Import_is_idempotent_and_never_overwrites_local_password()
    {
        var connectionString = _postgres.GetConnectionString();
        var coreFactory = new CoreContextFactory(connectionString);
        var skidFactory = new SkidContextFactory(connectionString);

        await using (var db = coreFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        // Схема СКИД чужая — поднимаем её руками, ровно те колонки, которые читает адаптер.
        var activeId = Guid.NewGuid();
        var blockedId = Guid.NewGuid();
        var tempPasswordId = Guid.NewGuid();
        var skidHash = PasswordHashing.Hash("пароль-из-скид");

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand(
                """
                create table users (
                    id uuid primary key,
                    login text not null,
                    password_hash text not null,
                    full_name text not null,
                    is_blocked boolean not null,
                    security_stamp text not null,
                    must_change_password boolean not null,
                    department_id uuid null);
                create table departments (id uuid primary key, code text null, name text null);
                """, connection);
            await create.ExecuteNonQueryAsync();

            await using var insert = new NpgsqlCommand(
                """
                insert into users (id, login, password_hash, full_name, is_blocked, security_stamp, must_change_password)
                values (@a, ' ivanov ', @h, 'Иванов И.И.', false, 'stamp-a', false),
                       (@b, 'petrov', @h, 'Петров П.П.', true,  'stamp-b', false),
                       (@t, 'temp',   @h, 'Времен Н.Й.', false, 'stamp-t', true);
                """, connection);
            insert.Parameters.AddWithValue("a", activeId);
            insert.Parameters.AddWithValue("b", blockedId);
            insert.Parameters.AddWithValue("t", tempPasswordId);
            insert.Parameters.AddWithValue("h", skidHash);
            await insert.ExecuteNonQueryAsync();
        }

        var importer = new SkidIdentityImporter(skidFactory, coreFactory);

        var first = await importer.ImportAsync();
        first.Read.ShouldBe(3);
        first.Created.ShouldBe(3);
        first.PasswordsImported.ShouldBe(3);
        first.SkippedAlreadyLocal.ShouldBe(0);

        // Перенесённой учёткой можно войти ПРЕЖНИМ паролем — ради этого хеши и переносятся.
        var provider = new LocalIdentityProvider(coreFactory);
        var identity = await provider.VerifyCredentialsAsync("ivanov", "пароль-из-скид");
        identity.ShouldNotBeNull();
        identity.DisplayName.ShouldBe("Иванов И.И.");
        identity.SecurityStamp.ShouldBe("stamp-a");

        // Логин очищается от пробелов при переносе (в СКИД он хранился с ними).
        await using (var db = coreFactory.CreateDbContext())
        {
            var users = await db.Users.OrderBy(u => u.UserName).ToListAsync();
            users.Select(u => u.UserName).ShouldBe(["ivanov", "petrov", "temp"]);

            // Блокировка СКИД переносится как отключение учётки.
            users.Single(u => u.UserName == "petrov").IsActive.ShouldBeFalse();

            // Признак временного пароля переносится: он известен администратору, выдавшему сброс.
            users.Single(u => u.UserName == "temp").MustChangePassword.ShouldBeTrue();
            users.Single(u => u.UserName == "ivanov").MustChangePassword.ShouldBeFalse();
        }

        // Заблокированный в СКИД войти не может (учётка отключена).
        (await provider.VerifyCredentialsAsync("petrov", "пароль-из-скид")).ShouldBeNull();

        // ЧЕЛОВЕК СМЕНИЛ ПАРОЛЬ У НАС — после перехода это штатный случай.
        await using (var db = coreFactory.CreateDbContext())
        {
            var user = await db.Users.FirstAsync(u => u.UserName == "ivanov");
            user.PasswordHash = PasswordHashing.Hash("новый-локальный");
            user.SecurityStamp = PasswordHashing.NewSecurityStamp();
            await db.SaveChangesAsync();
        }

        // Повторный запуск переноса НЕ должен вернуть старый пароль.
        var second = await importer.ImportAsync();
        second.Created.ShouldBe(0);
        second.PasswordsImported.ShouldBe(0);
        second.SkippedAlreadyLocal.ShouldBe(3);

        (await provider.VerifyCredentialsAsync("ivanov", "новый-локальный")).ShouldNotBeNull();
        (await provider.VerifyCredentialsAsync("ivanov", "пароль-из-скид")).ShouldBeNull();
    }

    [Fact(DisplayName = "После переключения вход привязывается к ТОЙ ЖЕ учётке, а не создаёт вторую")]
    public async Task Local_login_binds_to_the_same_account_after_import()
    {
        var connectionString = _postgres.GetConnectionString();
        var coreFactory = new CoreContextFactory(connectionString);

        // Учётка в состоянии «перенесена из СКИД»: ExternalId — идентификатор СКИД, пароль локальный.
        var skidExternalId = Guid.NewGuid().ToString("D");
        int userId;
        await using (var db = coreFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var user = new AppUserEntity
            {
                UserName = "migrated",
                ExternalId = skidExternalId,
                DisplayName = "Перенесённый П.П.",
                PasswordHash = PasswordHashing.Hash("пароль"),
                SecurityStamp = "stamp-migrated",
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var provider = new LocalIdentityProvider(coreFactory);
        var identity = await provider.VerifyCredentialsAsync("migrated", "пароль");

        // ГЛАВНОЕ: возвращается СОХРАНЁННЫЙ идентификатор, а не наш внутренний номер. Вход
        // (LoginService) ищет локального субъекта строго по нему; верни провайдер свой Id —
        // совпадения бы не нашлось, имя входа оказалось бы занято, и вход отказал бы ВСЕМ
        // перенесённым учёткам сразу после переключения Auth:Provider=Local.
        identity.ShouldNotBeNull();
        identity.ExternalId.ShouldBe(skidExternalId);
        identity.ExternalId.ShouldNotBe(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Ревалидация сессии ищет по тому же значению — иначе живая сессия рвалась бы на первой проверке.
        var state = await provider.GetStateAsync(skidExternalId);
        state.ShouldNotBeNull();
        state.IsBlocked.ShouldBeFalse();
        state.SecurityStamp.ShouldBe("stamp-migrated");
    }

    private sealed class SkidContextFactory(string connectionString) : IDbContextFactory<SkidDbContext>
    {
        public SkidDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<SkidDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options);
    }

    private sealed class CoreContextFactory(string connectionString) : IDbContextFactory<CoreDbContext>
    {
        public CoreDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(connectionString, npg =>
                {
                    npg.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
                    npg.UseVector();
                })
                .UseSnakeCaseNamingConvention()
                .Options);
    }
}
