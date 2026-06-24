using ISC.AI.AI.Security;
using ISC.AI.Abstractions.Security;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Тесты решётки доступа (ТБ-020/021/002, ADR-0014): неизменяемый ядровой floor режет материал выше
/// допуска и вне разрешённых подразделений; политика по умолчанию не расширяет доступ; без субъекта —
/// fail-closed. Проверяется ЛОГИКА предиката (in-memory); трансляция в SQL и невыдача выше допуска —
/// интеграционно на Э3-05 / GATE-1.
/// </summary>
public sealed class AccessPolicyTests
{
    // Минимальный режимный ресурс для проверки предиката.
    private sealed record Resource(short Classification, int DivisionId) : IClassified;

    [Fact(DisplayName = "Ядровой floor: материал выше допуска и из чужого подразделения отсекается")]
    public void Baseline_excludes_above_clearance_and_foreign_division()
    {
        var subject = new AccessContext("u1", MaxClassification: 2, AllowedDivisions: [7]);
        var allowed = BaselineAccess.Filter<Resource>(subject).Compile();

        allowed(new Resource(2, 7)).ShouldBeTrue();   // на уровне допуска, своё подразделение
        allowed(new Resource(1, 7)).ShouldBeTrue();   // ниже допуска
        allowed(new Resource(3, 7)).ShouldBeFalse();  // ВЫШЕ допуска — нельзя
        allowed(new Resource(2, 9)).ShouldBeFalse();  // чужое подразделение — нельзя
    }

    [Fact(DisplayName = "Профиль без грифов: один уровень — floor пропускает (вырожденный случай)")]
    public void Baseline_degenerate_single_level_passes()
    {
        var subject = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [0]);
        var allowed = BaselineAccess.Filter<Resource>(subject).Compile();

        allowed(new Resource(0, 0)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Политика по умолчанию не добавляет ограничений сверх floor'а")]
    public void Default_policy_allows_all()
    {
        var subject = new AccessContext("u1", 5, [1, 2, 3]);
        var policy = new AllowAllAccessPolicy().BuildFilter<Resource>(subject).Compile();

        policy(new Resource(5, 2)).ShouldBeTrue();
    }

    [Fact(DisplayName = "fail-closed: без субъекта floor не строится")]
    public void Baseline_requires_subject()
    {
        Should.Throw<ArgumentNullException>(() => BaselineAccess.Filter<Resource>(null!));
    }
}
