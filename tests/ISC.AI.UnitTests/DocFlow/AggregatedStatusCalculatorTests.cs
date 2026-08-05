using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Shouldly;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Агрегированный статус документа (Э4-35 этап 2.1, ТЗ СКИД §4.3): таблица приоритетов переносится
/// из СКИД 1:1 — тесты фиксируют каждую строку правила и унаследованные особенности.
/// </summary>
public sealed class AggregatedStatusCalculatorTests
{
    [Fact(DisplayName = "Без назначений — «не применимо» (Хранение либо Исполнение до назначений)")]
    public void Empty_list_yields_not_applicable() =>
        AggregatedStatusCalculator.Calculate([]).ShouldBe(DocumentAggregatedStatus.NotApplicable);

    [Fact(DisplayName = "Хотя бы одно «Просрочено» побеждает всё")]
    public void Any_overdue_wins() =>
        AggregatedStatusCalculator
            .Calculate([AssignmentStatus.Done, AssignmentStatus.InProgress, AssignmentStatus.Overdue])
            .ShouldBe(DocumentAggregatedStatus.Overdue);

    [Fact(DisplayName = "«Частично исполнено» сильнее «В работе»")]
    public void Partially_done_beats_in_progress() =>
        AggregatedStatusCalculator
            .Calculate([AssignmentStatus.InProgress, AssignmentStatus.PartiallyDone])
            .ShouldBe(DocumentAggregatedStatus.PartiallyDone);

    [Fact(DisplayName = "«Контроль» приравнивается к «В работе»")]
    public void In_control_counts_as_in_progress() =>
        AggregatedStatusCalculator
            .Calculate([AssignmentStatus.Registered, AssignmentStatus.InControl])
            .ShouldBe(DocumentAggregatedStatus.InProgress);

    [Fact(DisplayName = "Все «Исполнено» — документ исполнен")]
    public void All_done_yields_done() =>
        AggregatedStatusCalculator
            .Calculate([AssignmentStatus.Done, AssignmentStatus.Done])
            .ShouldBe(DocumentAggregatedStatus.Done);

    [Fact(DisplayName = "Все «Снято с контроля» — документ снят с контроля")]
    public void All_closed_yields_closed() =>
        AggregatedStatusCalculator
            .Calculate([AssignmentStatus.Closed, AssignmentStatus.Closed])
            .ShouldBe(DocumentAggregatedStatus.Closed);

    [Fact(DisplayName = "Только «Зарегистрировано» — документ зарегистрирован")]
    public void All_registered_yields_registered() =>
        AggregatedStatusCalculator
            .Calculate([AssignmentStatus.Registered, AssignmentStatus.Registered])
            .ShouldBe(DocumentAggregatedStatus.Registered);

    [Fact(DisplayName = "Смесь «Исполнено»+«Снято» — «Зарегистрирован» (особенность СКИД, сохранена 1:1)")]
    public void Mixed_done_and_closed_falls_back_to_registered() =>
        AggregatedStatusCalculator
            .Calculate([AssignmentStatus.Done, AssignmentStatus.Closed])
            .ShouldBe(DocumentAggregatedStatus.Registered);
}
