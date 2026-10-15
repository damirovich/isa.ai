using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Кэш волны аудиозаписи в строке носителя (ADR-0047, миграция <c>AudioWaveform</c>) на настоящем Postgres: волны нет —
/// пусто; сохранённая читается побайтно; волна прежней версии алгоритма не отдаётся (строится заново); повторное
/// сохранение перезаписывает; чужой носитель не задевается; носителя нет — сохранение ничего не делает.
/// </summary>
[Trait("Category", "Gate")]
public sealed class MediaWaveformCacheTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Волна: нет — null; сохранили — та же побайтно; прежняя версия — null; перезапись; соседний носитель не задет")]
    public async Task Cache_round_trips_and_ignores_stale_versions()
    {
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        int target, other;
        await using (var db = media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            target = await SeedAudioAsync(db, "target");
            other = await SeedAudioAsync(db, "other");
        }

        var cache = new MediaWaveformCache(media);
        (await cache.GetAsync(target)).ShouldBeNull();

        var first = new AudioWaveform(60, [0, 12, 255, 7], AudioWaveform.CurrentVersion);
        await cache.SaveAsync(target, first);
        var read = (await cache.GetAsync(target)).ShouldNotBeNull();
        read.StepMs.ShouldBe(60);
        read.Peaks.ShouldBe(first.Peaks);
        (await cache.GetAsync(other)).ShouldBeNull();

        var second = new AudioWaveform(20, [1, 2], AudioWaveform.CurrentVersion);
        await cache.SaveAsync(target, second);
        (await cache.GetAsync(target)).ShouldNotBeNull().Peaks.ShouldBe(second.Peaks);

        // Волна прежней версии алгоритма — как будто её нет: эндпоинт построит её заново.
        await using (var db = media.CreateDbContext())
        {
            await db.Assets.Where(a => a.Id == target)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.WaveformVersion, (short?)(AudioWaveform.CurrentVersion - 1)));
        }

        (await cache.GetAsync(target)).ShouldBeNull();

        // Носителя нет — сохранение ничего не делает и не падает.
        await cache.SaveAsync(999_999, first);
    }

    private static async Task<int> SeedAudioAsync(MediaDbContext db, string name)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Audio,
            OriginalFileName = name + ".mp3",
            StoredFileName = name + ".mp3",
            ContentType = "audio/mpeg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            DurationMs = 2000,
            Classification = 1,
            DivisionId = 7,
            IndexStatus = MediaIndexStatus.NotApplicable,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }
}
