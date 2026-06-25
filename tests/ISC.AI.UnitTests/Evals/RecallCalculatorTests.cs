using ISC.AI.Evals;
using Shouldly;

namespace ISC.AI.UnitTests.Evals;

/// <summary>Расчёт recall@k (Э4-05, КИ-03): математика и РАЗДЕЛЬНОСТЬ по языку (ky не маскируется ru).</summary>
public sealed class RecallCalculatorTests
{
    [Fact(DisplayName = "Recall случая: доля найденных релевантных; пустой эталон = 1.0")]
    public void Case_recall_math()
    {
        RecallCalculator.CaseRecall([1, 3], [1, 2]).ShouldBe(0.5);
        RecallCalculator.CaseRecall([1, 2, 3], [1, 2]).ShouldBe(1.0);
        RecallCalculator.CaseRecall([9], [1, 2]).ShouldBe(0.0);
        RecallCalculator.CaseRecall([1], []).ShouldBe(1.0);
    }

    [Fact(DisplayName = "Recall раздельно ru/ky: провал ky НЕ маскируется русским (два числа, КИ-03)")]
    public void Recall_is_separated_by_language()
    {
        (string Language, double Recall)[] caseRecalls =
        [
            ("ru", 1.0), ("ru", 1.0),
            ("ky", 0.0), ("ky", 0.5),
        ];

        var report = RecallCalculator.ByLanguage(caseRecalls);

        report.Count.ShouldBe(2);
        report.First(r => r.Language == "ru").RecallAtK.ShouldBe(1.0);
        // (0.0 + 0.5)/2 = 0.25 — низкий ky виден ОТДЕЛЬНО, не усреднён с русским.
        report.First(r => r.Language == "ky").RecallAtK.ShouldBe(0.25);
        report.First(r => r.Language == "ky").Cases.ShouldBe(2);
    }
}
