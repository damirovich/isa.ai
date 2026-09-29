using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Подтвердить или исправить дату и время съёмки уже загруженного носителя (ТФ-МЕД-17): у носителей, загруженных до
/// того, как реквизит стал обязательным, его нет; у остальных оператор исправляет ошибку ввода или сбитые часы
/// камеры. Право — то же, что на загрузку (Следователь, Администратор); носитель — только из дел субъекта (ТБ-071).
/// </summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="CapturedAt">Дата и время съёмки (со смещением пояса; хранится в UTC).</param>
public sealed record SetAssetCapturedAtCommand(int AssetId, DateTimeOffset CapturedAt) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"media:asset:{AssetId}:captured-at:{CapturedAt.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}";

    /// <inheritdoc cref="SetAssetCapturedAtCommand" />
    public sealed class Handler(
        IMediaAdministration administration, IAccessContextProvider accessProvider, ICaseScope caseScope, IMediaStore store)
        : IRequestHandler<SetAssetCapturedAtCommand, ResponseDto<bool>>
    {
        private const string NotFound = "Носитель не найден или недоступен.";

        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetAssetCapturedAtCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest("Реквизиты носителя ведут роли Следователь и Администратор.");
            }

            // Fail-closed (ТБ-020/021, ТБ-071): носитель вне дел субъекта неотличим от несуществующего.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (!await caseScope.IsAssetAccessibleAsync(command.AssetId, access, cancellationToken))
            {
                return ResponseDto<bool>.NotFound(NotFound);
            }

            return await store.SetCapturedAtAsync(command.AssetId, command.CapturedAt, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound(NotFound);
        }
    }
}

/// <summary>Дата съёмки не в будущем.</summary>
public sealed class SetAssetCapturedAtValidator : AbstractValidator<SetAssetCapturedAtCommand>
{
    /// <inheritdoc cref="SetAssetCapturedAtValidator" />
    public SetAssetCapturedAtValidator()
    {
        RuleFor(c => c.AssetId).GreaterThan(0);
        RuleFor(c => c.CapturedAt)
            .Must(value => value <= DateTimeOffset.UtcNow.AddDays(1))
            .WithMessage("Дата съёмки не может быть в будущем.");
    }
}
