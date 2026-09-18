using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Приём носителя (ТС-010, ТФ-МЕД-01) на реальном Postgres: дата съёмки со смещением пояса сервера
/// нормализуется к UTC (Npgsql принимает <c>timestamptz</c> только со смещением 0 — иначе загрузка
/// падала), а дедупликация по хешу учитывает ГРИФ (ТБ-070/074): тот же файл под другим грифом — второй
/// носитель, под тем же — «уже есть».
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaStoreReceiveTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ТФ-МЕД-01: дата съёмки с +06:00 принимается и читается как тот же момент в UTC")]
    public async Task CapturedAt_with_local_offset_is_stored_as_utc()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var store = new MediaStore(factory, new RecordingFileStorage());
        var captured = new DateTimeOffset(2026, 9, 17, 14, 30, 0, TimeSpan.FromHours(6));

        var receipt = await store.ReceiveAsync(
            Draft("s.png", classification: 1) with { CapturedAt = captured },
            new MemoryStream([1, 2, 3]));
        receipt.Duplicate.ShouldBeFalse();

        await using (var db = factory.CreateDbContext())
        {
            var stored = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == receipt.AssetId);
            stored.CapturedAt.ShouldNotBeNull();
            stored.CapturedAt.Value.ShouldBe(captured); // тот же момент времени (сравнение DateTimeOffset — по UtcDateTime)
            stored.CapturedAt.Value.Offset.ShouldBe(TimeSpan.Zero);
            stored.CapturedAt.Value.UtcDateTime.ShouldBe(new DateTime(2026, 9, 17, 8, 30, 0, DateTimeKind.Utc));
        }

        // Без даты съёмки — как и раньше, null.
        var withoutDate = await store.ReceiveAsync(Draft("t.png", classification: 1), new MemoryStream([4, 5, 6]));
        await using (var db = factory.CreateDbContext())
        {
            (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == withoutDate.AssetId)).CapturedAt.ShouldBeNull();
        }
    }

    [Fact(DisplayName = "ТБ-070/074: дедупликация по хешу — в пределах подразделения И грифа: другой гриф — второй носитель, тот же — «уже есть»")]
    public async Task Dedup_key_includes_classification()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var store = new MediaStore(factory, new RecordingFileStorage());
        byte[] content = [10, 20, 30, 40];

        var open = await store.ReceiveAsync(Draft("x.png", classification: 0), new MemoryStream(content));
        open.Duplicate.ShouldBeFalse();

        // Тот же файл в дело с грифом 2 (гриф носителя = гриф дела): отдельный носитель — иначе биометрия
        // секретного дела жила бы под грифом 0 либо носитель был бы невидим для дела с меньшим грифом.
        var secret = await store.ReceiveAsync(Draft("x.png", classification: 2), new MemoryStream(content));
        secret.Duplicate.ShouldBeFalse();
        secret.AssetId.ShouldNotBe(open.AssetId);

        // Тот же файл под тем же грифом — дубликат существующего носителя того же грифа.
        var again = await store.ReceiveAsync(Draft("y.png", classification: 2), new MemoryStream(content));
        again.Duplicate.ShouldBeTrue();
        again.AssetId.ShouldBe(secret.AssetId);

        var againOpen = await store.ReceiveAsync(Draft("z.png", classification: 0), new MemoryStream(content));
        againOpen.Duplicate.ShouldBeTrue();
        againOpen.AssetId.ShouldBe(open.AssetId);

        // Другое подразделение — свой носитель (ключ по подразделению сохранён).
        var otherDivision = await store.ReceiveAsync(
            Draft("x.png", classification: 0) with { DivisionId = 8 }, new MemoryStream(content));
        otherDivision.Duplicate.ShouldBeFalse();

        await using (var db = factory.CreateDbContext())
        {
            var rows = await db.Assets.AsNoTracking().ToListAsync();
            rows.Count.ShouldBe(3);
            rows.Select(a => a.ContentHash).Distinct().ShouldHaveSingleItem();
            rows.Single(a => a.Id == secret.AssetId).Classification.ShouldBe<short>(2);
            rows.Single(a => a.Id == open.AssetId).Classification.ShouldBe<short>(0);
        }
    }

    private static MediaAssetDraft Draft(string fileName, short classification) => new(
        OriginalFileName: fileName,
        ContentType: "image/png",
        Kind: MediaKind.Image,
        Classification: classification,
        DivisionId: 7,
        Source: "тест",
        CapturedAt: null,
        UploadedByUserId: 10);
}
