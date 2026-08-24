using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Автопросрочка устранения (ТФ-МОН-01) на настоящем PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Ключевой инвариант: система переводит в «Просрочено» ТОЛЬКО неустранённые нарушения с истёкшим
/// контрольным сроком. «Устранено» не трогается (задним числом статус не портим), будущий срок и
/// отсутствие срока — не кандидаты, повторный прогон ничего не делает (идемпотентность).
/// </remarks>
public sealed class RemediationDeadlineTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 6, 1);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Просрочку получают только неустранённые с истёкшим сроком; повторный прогон пуст")]
    public async Task Marks_only_unresolved_with_expired_deadline()
    {
        var (store, inspector, divisionId, kindId) = await BuildAsync();

        var expired = Today.AddDays(-1);
        var future = Today.AddDays(5);
        var underControlId = await SeedAsync(inspector, divisionId, kindId, RemediationStatus.UnderControl, expired);
        var partialId = await SeedAsync(inspector, divisionId, kindId, RemediationStatus.Partial, expired);
        var resolvedId = await SeedAsync(inspector, divisionId, kindId, RemediationStatus.Resolved, expired);
        var futureId = await SeedAsync(inspector, divisionId, kindId, RemediationStatus.UnderControl, future);
        var noDeadlineId = await SeedAsync(inspector, divisionId, kindId, RemediationStatus.UnderControl, null);

        var marked = await store.MarkOverdueAsync(Today);

        marked.Select(m => m.ViolationId).Order().ShouldBe(new[] { underControlId, partialId }.Order());
        marked.ShouldAllBe(m => m.DivisionId == divisionId && m.Deadline == expired);

        await using (var db = inspector.CreateDbContext())
        {
            var statuses = await db.Violations.AsNoTracking()
                .ToDictionaryAsync(v => v.Id, v => v.RemediationStatus);
            statuses[underControlId].ShouldBe(RemediationStatus.Overdue);
            statuses[partialId].ShouldBe(RemediationStatus.Overdue);
            statuses[resolvedId].ShouldBe(RemediationStatus.Resolved);
            statuses[futureId].ShouldBe(RemediationStatus.UnderControl);
            statuses[noDeadlineId].ShouldBe(RemediationStatus.UnderControl);
        }

        // Идемпотентность: уже помеченные — не кандидаты.
        (await store.MarkOverdueAsync(Today)).ShouldBeEmpty();
    }

    private async Task<(ViolationStore Store, InspectorContextFactory Inspector, int DivisionId, int KindId)> BuildAsync()
    {
        await using (var db = new CoreContextFactory(_postgres.GetConnectionString()).CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var inspector = new InspectorContextFactory(_postgres.GetConnectionString());
        await using (var db = inspector.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            var division = new Division { Name = "Альфа" };
            var sphere = new ViolationCategory { Name = "Документооборот" };
            db.Divisions.Add(division);
            db.ViolationCategories.Add(sphere);
            await db.SaveChangesAsync();

            var kind = new ViolationCategory { Name = "Просрочка регистрации", ParentId = sphere.Id };
            db.ViolationCategories.Add(kind);
            await db.SaveChangesAsync();

            return (new ViolationStore(inspector), inspector, division.Id, kind.Id);
        }
    }

    private static async Task<int> SeedAsync(
        InspectorContextFactory inspector, int divisionId, int kindId,
        RemediationStatus status, DateOnly? deadline)
    {
        await using var db = inspector.CreateDbContext();
        var violation = new Violation
        {
            DivisionId = divisionId,
            CategoryId = kindId,
            Severity = ViolationSeverity.Medium,
            DetectedAt = new DateOnly(2026, 5, 1),
            RemediationStatus = status,
            RemediationDeadline = deadline,
        };
        db.Violations.Add(violation);
        await db.SaveChangesAsync();
        return violation.Id;
    }
}
