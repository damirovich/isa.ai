using FluentValidation;

namespace ISC.AI.Modules.Media.Application.Features.Assets.Commands.SnapshotFrame;

/// <summary>
/// Правила снимка кадра (ADR-0028): носитель указан, момент неотрицателен. Верхняя граница — длительность
/// носителя — известна только обработчику и проверяется им.
/// </summary>
public sealed class SnapshotFrameValidator : AbstractValidator<SnapshotFrameCommand>
{
    /// <inheritdoc cref="SnapshotFrameValidator" />
    public SnapshotFrameValidator()
    {
        RuleFor(c => c.AssetId).GreaterThan(0);
        RuleFor(c => c.TimestampMs).GreaterThanOrEqualTo(0)
            .WithMessage("Момент записи не может быть отрицательным.");
    }
}
