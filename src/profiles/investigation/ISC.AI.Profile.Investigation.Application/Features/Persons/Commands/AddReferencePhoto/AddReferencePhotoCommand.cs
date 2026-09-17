using System.Globalization;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>
/// Добавить эталонное изображение фигуранта (ТФ-ПЕР-01, ТБ-077): ссылка по значению на носитель/лицо в схеме
/// <c>media</c>, источник, законное основание хранения, дата пересмотра. Если указан <paramref name="SupersedesId"/>,
/// прежний эталон помечается заменённым, но НЕ удаляется (ТБ-077) — замена фиксируется в журнале с обоими
/// идентификаторами. Хеш файла и «кто/когда» ведёт пакет «Медиа» по носителю. <c>QualityScore</c> — оценка
/// качества лица (ТО-мат-07), если страница получила её от пакета «Медиа».
/// </summary>
public sealed record AddReferencePhotoCommand(
    int PersonId,
    int MediaAssetId,
    int? MediaFaceId = null,
    string? Source = null,
    string? LegalBasis = null,
    DateOnly? ReviewDueAt = null,
    int? SupersedesId = null,
    float? QualityScore = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Смена эталона — событие аудита с указанием заменяемого (ТБ-077).</remarks>
    public string? AuditSummary =>
        $"investigation:person:{PersonId}:reference-photo:add:asset={MediaAssetId};face={MediaFaceId?.ToString(CultureInfo.InvariantCulture) ?? "-"};"
        + $"supersedes={SupersedesId?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    /// <inheritdoc cref="AddReferencePhotoCommand" />
    public sealed class Handler(
        IPersonStore persons, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<AddReferencePhotoCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(AddReferencePhotoCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            // Fail-closed (ТБ-021): фигурант должен быть доступен; гриф эталона хранилище берёт у дела и
            // не ниже грифа носителя (ТБ-070).
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var draft = new ReferencePhotoDraft(
                command.PersonId, command.MediaAssetId, command.MediaFaceId, command.QualityScore,
                string.IsNullOrWhiteSpace(command.Source) ? null : command.Source.Trim(),
                string.IsNullOrWhiteSpace(command.LegalBasis) ? null : command.LegalBasis.Trim(),
                command.ReviewDueAt, access.NumericSubjectId);

            var (result, photoId) = await persons.AddReferencePhotoAsync(
                draft, command.SupersedesId, access, cancellationToken);
            return result == PersonWriteResult.Ok
                ? ResponseDto<int>.Ok(photoId)
                : ResponseDto<int>.NotFound(PersonGuard.NotFound);
        }
    }
}

/// <summary>Правила формы эталона.</summary>
public sealed class AddReferencePhotoValidator : AbstractValidator<AddReferencePhotoCommand>
{
    /// <summary>Фигурант и носитель обязательны; необязательные идентификаторы положительные; тексты ограничены.</summary>
    public AddReferencePhotoValidator()
    {
        RuleFor(c => c.PersonId).GreaterThan(0);
        RuleFor(c => c.MediaAssetId).GreaterThan(0).WithMessage("Укажите носитель эталона.");
        RuleFor(c => c.MediaFaceId).GreaterThan(0).When(c => c.MediaFaceId is not null);
        RuleFor(c => c.SupersedesId).GreaterThan(0).When(c => c.SupersedesId is not null);
        RuleFor(c => c.QualityScore).InclusiveBetween(0f, 1f).When(c => c.QualityScore is not null);
        RuleFor(c => c.Source).MaximumLength(500);
        RuleFor(c => c.LegalBasis).MaximumLength(1000);
    }
}
