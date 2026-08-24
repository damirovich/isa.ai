using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Risk;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Источник сигналов риска и сводки дашборда (Э5-01 шаг 2) на настоящем PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Главная проверка — ДЕТЕРМИНИЗМ приёмки (§5.2.5.1): по известному набору фактов балл обязан сойтись
/// с ручным расчётом по формуле Приложения §2 (веса 1/1.5/2/1, тяжесть 1/2/4/8, пороги 10/25/50).
/// Разошёлся — значит, сигналы (открытые тяжести, повторы, просрочки, тренд) считаются не по определению.
/// </remarks>
public sealed class RiskDataSourceTests : IAsyncLifetime
{
    private static readonly DateOnly From = new(2026, 5, 1);
    private static readonly DateOnly To = new(2026, 5, 31);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Дашборд: счётчики периода, ручной балл риска и порядок светофора сходятся")]
    public async Task Dashboard_matches_hand_computed_score()
    {
        var (source, ids) = await BuildAsync();

        // «Альфа», вид K1: цепочка повторов — High/просрочено, затем Medium/устранено,
        // затем Critical/на контроле. «Бета», вид K2: единичное устранённое.
        await SeedViolationAsync(ids.DivisionA, ids.KindK1, ViolationSeverity.High,
            new DateOnly(2026, 5, 10), RemediationStatus.Overdue);
        await SeedViolationAsync(ids.DivisionA, ids.KindK1, ViolationSeverity.Medium,
            new DateOnly(2026, 5, 20), RemediationStatus.Resolved);
        await SeedViolationAsync(ids.DivisionA, ids.KindK1, ViolationSeverity.Critical,
            new DateOnly(2026, 5, 25), RemediationStatus.UnderControl);
        await SeedViolationAsync(ids.DivisionB, ids.KindK2, ViolationSeverity.Low,
            new DateOnly(2026, 5, 5), RemediationStatus.Resolved);

        // Предыдущий период (для тренда «Альфы»: 3 − 1 = 2). В окно [From, To] не входит.
        await SeedViolationAsync(ids.DivisionA, ids.KindK2, ViolationSeverity.Low,
            new DateOnly(2026, 4, 15), RemediationStatus.Resolved);

        var summary = await source.GetDashboardAsync(From, To);

        // Счётчики периода — апрельская запись (15.04) в окно не входит.
        summary.TotalViolations.ShouldBe(4);
        summary.CriticalViolations.ShouldBe(1);
        summary.Remediated.ShouldBe(2);
        summary.NotRemediated.ShouldBe(2);

        // «Альфа» вручную: открытые тяжести High+Critical = 4+8 = 12 (w1=1), повторы 2 (w2=1.5),
        // просрочки 1 (w3=2), тренд 3−1=2 (w4=1) → 12 + 3 + 2 + 2 = 19 → средний → жёлтый.
        summary.DivisionRisks.Count.ShouldBe(2);
        var alpha = summary.DivisionRisks[0];
        alpha.DivisionName.ShouldBe("Альфа");
        alpha.ViolationCount.ShouldBe(3);
        alpha.Assessment.Score.ShouldBe(19);
        alpha.Assessment.Level.ShouldBe(RiskLevel.Medium);
        alpha.Assessment.Color.ShouldBe(RiskColor.Yellow);

        // «Бета»: открытых нет, повторов нет, тренд 1−0=1 → 1 → низкий → зелёный. Сортировка — по баллу.
        var beta = summary.DivisionRisks[1];
        beta.DivisionName.ShouldBe("Бета");
        beta.Assessment.Score.ShouldBe(1);
        beta.Assessment.Color.ShouldBe(RiskColor.Green);

        // Лента: только период, свежие первыми.
        summary.RecentViolations.Count.ShouldBe(4);
        summary.RecentViolations[0].Severity.ShouldBe(ViolationSeverity.Critical);
        summary.RecentViolations[0].CategoryName.ShouldBe("Просрочка регистрации");
        summary.RecentViolations.Select(r => r.DetectedAt).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact(DisplayName = "Разбор риска: сигналы, болевые сферы и последняя рекомендация сходятся с ручным расчётом")]
    public async Task Division_risk_breakdown_matches_hand_computed_signals()
    {
        var (source, ids) = await BuildAsync();

        await SeedViolationAsync(ids.DivisionA, ids.KindK1, ViolationSeverity.High,
            new DateOnly(2026, 5, 10), RemediationStatus.Overdue, "Усилить контроль сроков");
        await SeedViolationAsync(ids.DivisionA, ids.KindK1, ViolationSeverity.Medium,
            new DateOnly(2026, 5, 20), RemediationStatus.Resolved, "Провести разбор с исполнителями");
        await SeedViolationAsync(ids.DivisionA, ids.KindK1, ViolationSeverity.Critical,
            new DateOnly(2026, 5, 25), RemediationStatus.UnderControl);
        await SeedViolationAsync(ids.DivisionB, ids.KindK2, ViolationSeverity.Low,
            new DateOnly(2026, 5, 5), RemediationStatus.Resolved);
        await SeedViolationAsync(ids.DivisionA, ids.KindK2, ViolationSeverity.Low,
            new DateOnly(2026, 4, 15), RemediationStatus.Resolved);

        var risks = await source.GetDivisionRisksAsync(From, To);

        risks.Count.ShouldBe(2);
        var alpha = risks[0];
        alpha.DivisionName.ShouldBe("Альфа");
        alpha.ViolationCount.ShouldBe(3);
        alpha.OpenCount.ShouldBe(2);          // High/просрочено + Critical/на контроле.
        alpha.RepeatCount.ShouldBe(2);
        alpha.OverdueCount.ShouldBe(1);
        alpha.TrendDelta.ShouldBe(2);         // 3 в мае − 1 в апреле.
        alpha.Assessment.Score.ShouldBe(19);  // Тот же ручной расчёт, что у дашборда.
        // Болевая точка — сфера (родитель вида), с числом нарушений.
        alpha.TopSpheres.ShouldBe(["Документооборот — 3"]);
        // Последняя НЕПУСТАЯ рекомендация: у самого свежего (25.05) её нет — берётся от 20.05.
        alpha.LastRecommendation.ShouldBe("Провести разбор с исполнителями");

        var beta = risks[1];
        beta.OpenCount.ShouldBe(0);
        beta.TrendDelta.ShouldBe(1);
        beta.LastRecommendation.ShouldBeNull();
    }

    [Fact(DisplayName = "Мониторинг устранения: счётчики по подразделениям и порядок карточек (неустранённые первыми)")]
    public async Task Remediation_summary_counts_and_orders_cards()
    {
        var (source, ids) = await BuildAsync();

        await SeedViolationAsync(ids.DivisionA, ids.KindK1, ViolationSeverity.High,
            new DateOnly(2026, 5, 10), RemediationStatus.Overdue, "Усилить контроль сроков");
        await SeedViolationAsync(ids.DivisionA, ids.KindK1, ViolationSeverity.Medium,
            new DateOnly(2026, 5, 20), RemediationStatus.Resolved);
        await SeedViolationAsync(ids.DivisionA, ids.KindK1, ViolationSeverity.Critical,
            new DateOnly(2026, 5, 25), RemediationStatus.UnderControl);
        await SeedViolationAsync(ids.DivisionB, ids.KindK2, ViolationSeverity.Low,
            new DateOnly(2026, 5, 5), RemediationStatus.Resolved);
        await SeedViolationAsync(ids.DivisionA, ids.KindK2, ViolationSeverity.Low,
            new DateOnly(2026, 4, 15), RemediationStatus.Resolved);

        var summary = await source.GetRemediationAsync(From, To);

        // Счётчики по подразделениям (по алфавиту); апрельская запись в окно не входит.
        summary.Divisions.Count.ShouldBe(2);
        var alpha = summary.Divisions[0];
        alpha.DivisionName.ShouldBe("Альфа");
        alpha.Total.ShouldBe(3);
        alpha.Resolved.ShouldBe(1);
        alpha.UnderControl.ShouldBe(1);
        alpha.Overdue.ShouldBe(1);
        alpha.Partial.ShouldBe(0);
        summary.Divisions[1].DivisionName.ShouldBe("Бета");
        summary.Divisions[1].Resolved.ShouldBe(1);

        // Карточки: неустранённые первыми (свежие выше), устранённые следом.
        summary.Rows.Count.ShouldBe(4);
        summary.Rows[0].RemediationStatus.ShouldBe(RemediationStatus.UnderControl);
        summary.Rows[1].RemediationStatus.ShouldBe(RemediationStatus.Overdue);
        summary.Rows[1].Recommendation.ShouldBe("Усилить контроль сроков");
        summary.Rows[2].RemediationStatus.ShouldBe(RemediationStatus.Resolved);
        summary.Rows[3].RemediationStatus.ShouldBe(RemediationStatus.Resolved);
    }

    [Fact(DisplayName = "Пустой период — нулевая сводка без строк светофора")]
    public async Task Empty_period_yields_zero_summary()
    {
        var (source, _) = await BuildAsync();

        var summary = await source.GetDashboardAsync(From, To);

        summary.TotalViolations.ShouldBe(0);
        summary.NotRemediated.ShouldBe(0);
        summary.DivisionRisks.ShouldBeEmpty();
        summary.RecentViolations.ShouldBeEmpty();
    }

    private async Task<(RiskDataSource Source, TestIds Ids)> BuildAsync()
    {
        await using (var db = new CoreContextFactory(_postgres.GetConnectionString()).CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var inspector = new InspectorContextFactory(_postgres.GetConnectionString());
        await using (var db = inspector.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            var divisionA = new Division { Name = "Альфа" };
            var divisionB = new Division { Name = "Бета" };
            var sphere = new ViolationCategory { Name = "Документооборот" };
            db.Divisions.AddRange(divisionA, divisionB);
            db.ViolationCategories.Add(sphere);
            await db.SaveChangesAsync();

            var kind1 = new ViolationCategory { Name = "Просрочка регистрации", ParentId = sphere.Id };
            var kind2 = new ViolationCategory { Name = "Утрата контроля", ParentId = sphere.Id };
            db.ViolationCategories.AddRange(kind1, kind2);
            await db.SaveChangesAsync();

            return (new RiskDataSource(inspector, new RiskScoreCalculator()),
                new TestIds(divisionA.Id, divisionB.Id, kind1.Id, kind2.Id));
        }
    }

    private async Task SeedViolationAsync(
        int divisionId, int categoryId, ViolationSeverity severity, DateOnly detectedAt, RemediationStatus status,
        string? recommendation = null)
    {
        var factory = new InspectorContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        db.Violations.Add(new Violation
        {
            DivisionId = divisionId,
            CategoryId = categoryId,
            Severity = severity,
            DetectedAt = detectedAt,
            RemediationStatus = status,
            Recommendation = recommendation,
        });
        await db.SaveChangesAsync();
    }

    private sealed record TestIds(int DivisionA, int DivisionB, int KindK1, int KindK2);
}
