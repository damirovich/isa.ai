using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Счётчики результативности распознавания (реализация <see cref="IMediaStatistics"/>, ТФ-ОТЧ-07, ADR-0061): четыре
/// сгруппированных запроса по подразделениям — носители, лица, сессии, кандидаты — под решёткой пакета.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021, ТБ-033): каждый запрос начинается с <see cref="MediaAccessFilters.VisibleTo{T}"/> —
/// того же floor'а и политики профиля, что каталог; гриф отчёта — максимум грифа ровно тех строк, что вошли в счёт.
/// </remarks>
public sealed class MediaStatistics(IDbContextFactory<MediaDbContext> contextFactory, IAccessPolicy accessPolicy) : IMediaStatistics
{
    /// <inheritdoc />
    public async Task<RecognitionStatistics> RecognitionAsync(
        DateTime fromUtc, DateTime toUtc, int? divisionId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Носители, проиндексированные в периоде: только фото и видео — у аудио лиц нет (ADR-0026).
        var assets = await db.Assets.AsNoTracking().VisibleTo(access, accessPolicy)
            .Where(a => a.IndexStatus == MediaIndexStatus.Indexed && a.IndexedAt >= fromUtc && a.IndexedAt < toUtc
                && (a.Kind == MediaKind.Image || a.Kind == MediaKind.Video)
                && (divisionId == null || a.DivisionId == divisionId))
            .GroupBy(a => a.DivisionId)
            .Select(g => new
            {
                DivisionId = g.Key,
                Images = g.Count(a => a.Kind == MediaKind.Image),
                Videos = g.Count(a => a.Kind == MediaKind.Video),
                MaxClassification = g.Max(a => a.Classification),
            })
            .ToListAsync(cancellationToken);

        // Лица этих же носителей; непригодные по качеству (ТО-мат-07) — «отбраковка».
        var faces = await db.Faces.AsNoTracking().VisibleTo(access, accessPolicy)
            .Where(f => f.Asset!.IndexStatus == MediaIndexStatus.Indexed && f.Asset.IndexedAt >= fromUtc && f.Asset.IndexedAt < toUtc
                && (f.Asset.Kind == MediaKind.Image || f.Asset.Kind == MediaKind.Video)
                && (divisionId == null || f.DivisionId == divisionId))
            .GroupBy(f => f.DivisionId)
            .Select(g => new
            {
                DivisionId = g.Key,
                Found = g.Count(),
                Rejected = g.Count(f => !f.QualityAcceptable),
                MaxClassification = g.Max(f => f.Classification),
            })
            .ToListAsync(cancellationToken);

        // Сессии поиска, начатые в периоде, — по подразделению того, кто искал.
        var visibleSessions = db.SearchSessions.AsNoTracking().VisibleTo(access, accessPolicy)
            .Where(s => s.CreatedAt >= fromUtc && s.CreatedAt < toUtc && (divisionId == null || s.DivisionId == divisionId));
        var sessions = await visibleSessions
            .GroupBy(s => s.DivisionId)
            .Select(g => new
            {
                DivisionId = g.Key,
                Searches = g.Count(),
                Auto = g.Count(s => s.Origin == SessionOrigin.SystemSuggestion),
                MaxClassification = g.Max(s => s.Classification),
            })
            .ToListAsync(cancellationToken);

        // Кандидаты этих сессий: кандидат виден сам И видна его сессия; исход — текущий статус верификации.
        var candidates = await (
                from c in db.SearchCandidates.AsNoTracking().VisibleTo(access, accessPolicy)
                join s in visibleSessions on c.SessionId equals s.Id
                select new { s.DivisionId, c.Status, c.Classification })
            .GroupBy(r => r.DivisionId)
            .Select(g => new
            {
                DivisionId = g.Key,
                Matches = g.Count(),
                Confirmed = g.Count(r => r.Status == CandidateStatus.Confirmed),
                Rejected = g.Count(r => r.Status == CandidateStatus.Rejected),
                Undetermined = g.Count(r => r.Status == CandidateStatus.Undetermined),
                Pending = g.Count(r => r.Status == CandidateStatus.Candidate || r.Status == CandidateStatus.PendingVerifier),
                MaxClassification = g.Max(r => r.Classification),
            })
            .ToListAsync(cancellationToken);

        var divisions = assets.Select(r => r.DivisionId)
            .Concat(faces.Select(r => r.DivisionId))
            .Concat(sessions.Select(r => r.DivisionId))
            .Concat(candidates.Select(r => r.DivisionId))
            .Distinct()
            .Order()
            .Select(id =>
            {
                var a = assets.FirstOrDefault(r => r.DivisionId == id);
                var f = faces.FirstOrDefault(r => r.DivisionId == id);
                var s = sessions.FirstOrDefault(r => r.DivisionId == id);
                var c = candidates.FirstOrDefault(r => r.DivisionId == id);
                return new RecognitionCounters(
                    id,
                    a?.Images ?? 0, a?.Videos ?? 0,
                    f?.Found ?? 0, f?.Rejected ?? 0,
                    s?.Searches ?? 0, s?.Auto ?? 0,
                    c?.Matches ?? 0, c?.Confirmed ?? 0, c?.Rejected ?? 0, c?.Undetermined ?? 0, c?.Pending ?? 0);
            })
            .ToList();

        var maxClassification = assets.Select(r => r.MaxClassification)
            .Concat(faces.Select(r => r.MaxClassification))
            .Concat(sessions.Select(r => r.MaxClassification))
            .Concat(candidates.Select(r => r.MaxClassification))
            .DefaultIfEmpty((short)0)
            .Max();

        return new RecognitionStatistics(divisions, maxClassification);
    }
}
