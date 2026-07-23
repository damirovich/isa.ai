using ISC.AI.Abstractions.Security;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Правило «числовой id субъекта для аудита» (ТБ-030 «кто»): числовой <c>SubjectId</c> идёт в журнал,
/// нечисловой (dev-заглушка и т.п.) — не пишется. Единая точка для <c>AuditBehavior</c> и записи
/// аудита генерации, чтобы разбор не расходился между ними.
/// </summary>
public sealed class AccessContextTests
{
    [Fact(DisplayName = "Числовой SubjectId → NumericSubjectId равен числу")]
    public void Numeric_subject_id_is_parsed()
    {
        new AccessContext("42", MaxClassification: 0, AllowedDivisions: [1])
            .NumericSubjectId.ShouldBe(42);
    }

    [Theory(DisplayName = "Нечисловой SubjectId → NumericSubjectId равен null (в аудит не пишется)")]
    [InlineData("dev")]
    [InlineData("u1")]
    [InlineData("")]
    [InlineData("4 2")]
    public void Non_numeric_subject_id_is_null(string subjectId)
    {
        new AccessContext(subjectId, MaxClassification: 0, AllowedDivisions: [1])
            .NumericSubjectId.ShouldBeNull();
    }
}
