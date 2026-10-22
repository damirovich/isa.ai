using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Хранилище настроек поиска и верификации на настоящем Postgres (<c>media.setting</c>, ADR-0054): порядок
/// «БД → конфигурация → умолчание», три значения одной записью, кеш сбрасывается после сохранения, мусор и рабочий
/// порог ниже границы не ослабляют правило, значения вне пределов не сохраняются.
/// </summary>
public sealed class MediaSettingsStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();
    private MediaContextFactory _media = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _media = new MediaContextFactory(_postgres.GetConnectionString());
        await using var db = _media.CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Пустая таблица и пустая конфигурация — умолчания поставки: один оператор, 70 %, без нижней границы, «не менялись»")]
    public async Task Empty_table_gives_defaults()
    {
        var settings = await Store().GetAsync();

        settings.Mode.ShouldBe(ConfirmationMode.SingleOperator);
        settings.ThresholdPercent.ShouldBe(70);
        settings.MinThresholdPercent.ShouldBe(1);
        settings.UpdatedAtUtc.ShouldBeNull();
    }

    [Fact(DisplayName = "Пока в базе ничего нет, действуют значения конфигурации; сохранённое в базе — главнее конфигурации")]
    public async Task Configuration_is_fallback_database_wins()
    {
        var configuration = Configuration(("Media:Verification:Mode", "two-person"), ("Media:Search:ThresholdPercent", "90"),
            ("Media:Search:MinThresholdPercent", "80"));

        var fromConfig = await Store(configuration).GetAsync();
        fromConfig.Mode.ShouldBe(ConfirmationMode.TwoPerson);
        (fromConfig.ThresholdPercent, fromConfig.MinThresholdPercent).ShouldBe((90, 80));

        (await Store(configuration).SaveAsync(ConfirmationMode.SingleOperator, 75, 72, changedByUserId: 5)).ShouldBeNull();
        var fromDb = await Store(configuration).GetAsync();
        fromDb.Mode.ShouldBe(ConfirmationMode.SingleOperator);
        (fromDb.ThresholdPercent, fromDb.MinThresholdPercent).ShouldBe((75, 72));
        fromDb.UpdatedByUserId.ShouldBe(5);
        fromDb.UpdatedAtUtc.ShouldNotBeNull().Kind.ShouldBe(DateTimeKind.Utc);

        await using var db = _media.CreateDbContext();
        (await db.Settings.AsNoTracking().OrderBy(s => s.Key).Select(s => s.Key).ToListAsync())
            .ShouldBe([MediaSettings.ThresholdKey, MediaSettings.MinThresholdKey, MediaSettings.ModeKey], ignoreOrder: true);
    }

    [Fact(DisplayName = "Кеш один на процесс: сохранение сбрасывает его — новое значение видно сразу, без перезапуска")]
    public async Task Save_invalidates_shared_cache()
    {
        var cache = new MediaSettingsCache();
        (await Store(cache: cache).GetAsync()).Mode.ShouldBe(ConfirmationMode.SingleOperator);

        (await Store(cache: cache).SaveAsync(ConfirmationMode.TwoPerson, 85, 70, changedByUserId: 1)).ShouldBeNull();

        (await Store(cache: cache).GetAsync()).Mode.ShouldBe(ConfirmationMode.TwoPerson);
    }

    [Fact(DisplayName = "Вне 40–99 % или рабочий ниже границы — отказ с пояснением, в базе ничего не меняется")]
    public async Task Invalid_values_are_not_saved()
    {
        var store = Store();
        (await store.SaveAsync(ConfirmationMode.SingleOperator, 100, 70, 1)).ShouldNotBeNull().ShouldContain("от 1 до 99 %");
        (await store.SaveAsync(ConfirmationMode.SingleOperator, 70, 85, 1)).ShouldNotBeNull().ShouldContain("ниже нижней границы");

        await using var db = _media.CreateDbContext();
        (await db.Settings.AnyAsync()).ShouldBeFalse();
    }

    [Fact(DisplayName = "Правка мимо экрана: мусор — «не задано», рабочий порог ниже границы — действует граница (строже)")]
    public async Task Garbage_and_inverted_values_never_weaken_rule()
    {
        await using (var db = _media.CreateDbContext())
        {
            db.Settings.AddRange(
                new MediaSetting { Key = MediaSettings.ModeKey, Value = "кто-нибудь", UpdatedAt = DateTime.UtcNow },
                new MediaSetting { Key = MediaSettings.ThresholdKey, Value = "60", UpdatedAt = DateTime.UtcNow },
                new MediaSetting { Key = MediaSettings.MinThresholdKey, Value = "75", UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var settings = await Store().GetAsync();
        settings.Mode.ShouldBe(ConfirmationMode.SingleOperator); // мусор → умолчание
        settings.MinThresholdPercent.ShouldBe(75);
        settings.ThresholdPercent.ShouldBe(75); // 60 ниже границы 75 → действует граница
    }

    [Fact(DisplayName = "ADR-0062 v0.3: «размечать дикторов при загрузке» — по умолчанию да; конфигурация, затем база; сохранение сбрасывает кеш, остальное не трогает")]
    public async Task Transcript_speakers_flag()
    {
        (await Store().GetAsync()).TranscriptSpeakers.ShouldBeTrue();
        (await Store(Configuration(("Media:Transcription:Speakers", "false"))).GetAsync()).TranscriptSpeakers.ShouldBeFalse();
        (await Store(Configuration(("Media:Transcription:Speakers", "мусор"))).GetAsync()).TranscriptSpeakers.ShouldBeTrue();

        var cache = new MediaSettingsCache();
        (await Store(cache: cache).GetAsync()).TranscriptSpeakers.ShouldBeTrue();
        await Store(cache: cache).SaveTranscriptSpeakersAsync(false, changedByUserId: 3);

        var saved = await Store(cache: cache).GetAsync();
        (saved.TranscriptSpeakers, saved.UpdatedByUserId, saved.Mode).ShouldBe((false, (int?)3, MediaSettings.DefaultMode));
        (await Store(Configuration(("Media:Transcription:Speakers", "true"))).GetAsync()).TranscriptSpeakers.ShouldBeFalse(); // база главнее

        await Store(cache: cache).SaveTranscriptSpeakersAsync(true, changedByUserId: 3);
        (await Store(cache: cache).GetAsync()).TranscriptSpeakers.ShouldBeTrue();
    }

    private MediaSettingsStore Store(IConfiguration? configuration = null, MediaSettingsCache? cache = null) =>
        new(_media, cache ?? new MediaSettingsCache(), configuration ?? new ConfigurationBuilder().Build());

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
