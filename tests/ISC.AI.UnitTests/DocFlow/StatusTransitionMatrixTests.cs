using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Shouldly;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Матрица переходов статуса назначения (Э4-35 этап 3.1, ТЗ СКИД §4.5): узкая матрица (решение
/// DL-052 СКИД), «Просрочено» и «Снято с контроля» вручную не покидаются.
/// </summary>
public sealed class StatusTransitionMatrixTests
{
    [Theory(DisplayName = "Допустимые переходы §4.5 разрешены")]
    [InlineData(AssignmentStatus.Registered, AssignmentStatus.InControl)]
    [InlineData(AssignmentStatus.Registered, AssignmentStatus.InProgress)]
    [InlineData(AssignmentStatus.InControl, AssignmentStatus.InProgress)]
    [InlineData(AssignmentStatus.InProgress, AssignmentStatus.Done)]
    [InlineData(AssignmentStatus.InProgress, AssignmentStatus.PartiallyDone)]
    [InlineData(AssignmentStatus.PartiallyDone, AssignmentStatus.InProgress)]
    [InlineData(AssignmentStatus.Done, AssignmentStatus.InProgress)]
    [InlineData(AssignmentStatus.Done, AssignmentStatus.Closed)]
    public void Allowed_transitions_pass(AssignmentStatus from, AssignmentStatus to) =>
        StatusTransitionMatrix.IsTransitionAllowed(from, to).ShouldBeTrue();

    [Theory(DisplayName = "Запрещённые переходы отклоняются (в т.ч. закрытые решением DL-052)")]
    [InlineData(AssignmentStatus.Registered, AssignmentStatus.Done)]
    [InlineData(AssignmentStatus.InControl, AssignmentStatus.Done)]
    [InlineData(AssignmentStatus.InControl, AssignmentStatus.PartiallyDone)]
    [InlineData(AssignmentStatus.InProgress, AssignmentStatus.InControl)]
    [InlineData(AssignmentStatus.PartiallyDone, AssignmentStatus.Done)]
    [InlineData(AssignmentStatus.InProgress, AssignmentStatus.Closed)]
    [InlineData(AssignmentStatus.InProgress, AssignmentStatus.Overdue)]
    public void Forbidden_transitions_rejected(AssignmentStatus from, AssignmentStatus to) =>
        StatusTransitionMatrix.IsTransitionAllowed(from, to).ShouldBeFalse();

    [Fact(DisplayName = "«Просрочено» и «Снято с контроля» вручную не покидаются")]
    public void Overdue_and_closed_are_manual_dead_ends()
    {
        StatusTransitionMatrix.AllowedFrom(AssignmentStatus.Overdue).ShouldBeEmpty();
        StatusTransitionMatrix.AllowedFrom(AssignmentStatus.Closed).ShouldBeEmpty();
    }
}
