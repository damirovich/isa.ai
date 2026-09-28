using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>
/// Пересечения фигуранта с другими делами экземпляра (ТФ-ПЕР-07, ADR-0029): вызывается при открытии карточки
/// и после правки адресов, транспорта и анкеты. Правило видимости (ТБ-084) — в хранилище: недоступный фигурант —
/// «не найден», дела выше допуска в ответе нет ни в каком виде.
/// </summary>
/// <param name="PersonId">Фигурант.</param>
public sealed record GetPersonIntersectionsQuery(int PersonId)
    : IRequest<ResponseDto<IReadOnlyList<IntersectionRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    /// <remarks>Только идентификатор фигуранта: значения реквизитов в сводку журнала не идут (ТБ-032).</remarks>
    public string? AuditSummary => $"investigation:person:{PersonId}:intersections:view";

    /// <inheritdoc cref="GetPersonIntersectionsQuery" />
    public sealed class Handler(IIntersectionStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<GetPersonIntersectionsQuery, ResponseDto<IReadOnlyList<IntersectionRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<IntersectionRow>>> Handle(
            GetPersonIntersectionsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска провайдер бросает; «нет» и «недоступен» — один ответ.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var rows = await store.FindForPersonAsync(query.PersonId, access, cancellationToken);
            return rows is null
                ? ResponseDto<IReadOnlyList<IntersectionRow>>.NotFound(PersonGuard.NotFound)
                : ResponseDto<IReadOnlyList<IntersectionRow>>.Ok(rows, rows.Count);
        }
    }
}

/// <summary>
/// Подтвердить или отклонить пересечение (ТФ-ПЕР-07, ADR-0029 п. 6). Решение — строка своего дела; связей между
/// делами система не создаёт. Принимают Следователь, Руководитель, Администратор (ТП-004).
/// </summary>
/// <param name="PersonId">Фигурант своего дела.</param>
/// <param name="Kind">Вид совпавшего реквизита.</param>
/// <param name="Key">Нормализованный ключ совпадения (из строки пересечения).</param>
/// <param name="OtherCaseId">Чужое дело.</param>
/// <param name="Decision">Решение.</param>
public sealed record ReviewIntersectionCommand(
    int PersonId, IntersectionKind Kind, string Key, int OtherCaseId, IntersectionDecision Decision)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <summary>Неразличимый ответ: фигурант недоступен или такого пересечения субъект не видит (ТБ-084).</summary>
    public const string NotFound = "Пересечение не найдено либо недоступно.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Идентификаторы и решение — без значения реквизита (ТБ-032).</remarks>
    public string? AuditSummary =>
        $"investigation:person:{PersonId}:intersection:{Kind}:case:{OtherCaseId}:{Decision}";

    /// <inheritdoc cref="ReviewIntersectionCommand" />
    public sealed class Handler(
        IIntersectionStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<ReviewIntersectionCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(ReviewIntersectionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var result = await store.ReviewAsync(
                command.PersonId, command.Kind, command.Key, command.OtherCaseId, command.Decision, access, cancellationToken);
            return result == PersonWriteResult.Ok
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound(NotFound);
        }
    }
}

/// <summary>Правила команды решения по пересечению: известные вид и решение, непустой ключ в пределах колонки.</summary>
public sealed class ReviewIntersectionValidator : AbstractValidator<ReviewIntersectionCommand>
{
    /// <summary>Предел длины ключа (колонка <c>key_normalized</c>).</summary>
    public const int MaxKeyLength = 1000;

    /// <inheritdoc cref="ReviewIntersectionValidator" />
    public ReviewIntersectionValidator()
    {
        RuleFor(c => c.PersonId).GreaterThan(0);
        RuleFor(c => c.OtherCaseId).GreaterThan(0);
        RuleFor(c => c.Kind).IsInEnum();
        RuleFor(c => c.Decision).IsInEnum();
        RuleFor(c => c.Key).NotEmpty().MaximumLength(MaxKeyLength);
    }
}
