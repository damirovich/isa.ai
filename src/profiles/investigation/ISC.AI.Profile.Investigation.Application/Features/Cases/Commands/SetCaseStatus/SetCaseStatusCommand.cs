using System.Globalization;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>
/// Сменить статус дела (ТФ-ДЕЛ-01). Закрытие ставит <c>ClosedAt</c> и ИСПОЛНЯЕТ регламент удаления
/// биометрических шаблонов дела с актом (ТФ-ДЕЛ-04, ТБ-074, GATE-6).
/// </summary>
/// <remarks>
/// ЧТО ИМЕННО УДАЛЯЕТСЯ. Шаблоны лиц (векторы, которыми ведётся поиск) и файлы вырезок — после
/// закрытия дела основания хранить биометрию нет (ТБ-074, ЦК ст. 80). Носители, кадры, сами лица с
/// таймкодами и привязками к фигурантам, решения верификации и документы дела СОХРАНЯЮТСЯ: это
/// материалы и результат расследования, они хранятся по правилам дела (ТФ-ДЕЛ-04 прямо это требует).
/// Проверяемое следствие: после закрытия прежняя проба не находит лиц этого дела (GATE-6), а «все
/// появления» фигуранта (ТФ-ПЕР-02) остаются на месте.
///
/// ПОРЯДОК И ЧТО БУДЕТ ПРИ СБОЕ. Сначала статус, затем удаление, затем акт. Обратный порядок означал бы
/// уничтожение биометрии у дела, которое в итоге не закрылось. Удаление fail-closed: недоступен журнал
/// аудита — ни один шаблон не снят, и акта не будет. Дело при этом уже закрыто, поэтому сценарий
/// возвращает явную ошибку: «дело закрыто, регламент не исполнен», а карточка дела показывает
/// предупреждение об отсутствии акта. Повторное закрытие исполняет регламент заново — операция
/// идемпотентна, второй раз удалять уже нечего.
/// </remarks>
public sealed record SetCaseStatusCommand(int CaseId, CaseStatus Status)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case:{CaseId}:status:{Status}";

    /// <inheritdoc cref="SetCaseStatusCommand" />
    public sealed class Handler(
        ICaseStore cases,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider,
        IMediaPurger purger)
        : IRequestHandler<SetCaseStatusCommand, ResponseDto<bool>>
    {
        /// <summary>Отказ, когда дело закрыто, а биометрия осталась: оператор обязан узнать об этом сразу.</summary>
        internal const string PurgeFailed =
            "Дело закрыто, но регламент удаления шаблонов НЕ исполнен (журнал аудита недоступен). "
            + "Шаблоны лиц дела остались в базе. Закройте дело повторно, когда журнал будет доступен.";

        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetCaseStatusCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await cases.SetStatusAsync(command.CaseId, command.Status, access, cancellationToken);
            if (result != CaseWriteResult.Ok || command.Status != CaseStatus.Closed)
            {
                return CaseGuard.ToResponse(result);
            }

            // Регламент ТБ-074 исполняется здесь же, а не фоном: «удалим потом» на режимных данных
            // означает «может не удалиться никогда», и подтвердить надзору будет нечем.
            var assetIds = await cases.ListMediaAssetIdsAsync([command.CaseId], cancellationToken);

            TemplatePurgeResult purge;
            try
            {
                purge = await purger.PurgeTemplatesAsync(
                    assetIds,
                    $"закрытие дела №{command.CaseId.ToString(CultureInfo.InvariantCulture)}",
                    access.NumericSubjectId,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Ловим широко намеренно: любая причина, по которой биометрия НЕ снята, — повод сказать
                // об этом оператору прямо, а не отдать «успешно закрыто». Сам сбой уходит в журнал
                // приложения сквозным поведением; наверх идёт понятная инструкция, что делать дальше.
                return ResponseDto<bool>.BadRequest(PurgeFailed);
            }

            await cases.SaveClosureActAsync(
                new CaseClosureActDraft(
                    command.CaseId,
                    access.NumericSubjectId,
                    MediaAssetsTotal: assetIds.Count,
                    purge.AssetsAffected,
                    purge.TemplatesRemoved,
                    purge.CropsRemoved),
                cancellationToken);

            return ResponseDto<bool>.Ok(true);
        }
    }
}

/// <summary>Правила смены статуса.</summary>
public sealed class SetCaseStatusValidator : AbstractValidator<SetCaseStatusCommand>
{
    /// <summary>Идентификатор положительный; статус — из перечисления.</summary>
    public SetCaseStatusValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.Status).IsInEnum().WithMessage("Неизвестный статус дела.");
    }
}
