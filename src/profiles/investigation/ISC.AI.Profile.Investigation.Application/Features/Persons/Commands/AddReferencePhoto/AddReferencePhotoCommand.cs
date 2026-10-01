using System.Collections.Frozen;
using System.Globalization;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
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
/// <remarks>
/// Идентификаторы носителя и лица приходят со страницы как числа и перебираемы; поэтому обработчик ПРОВЕРЯЕТ
/// через порты пакета «Медиа», что носитель доступен субъекту и привязан именно к делу фигуранта, а лицо (если
/// указано) принадлежит этому носителю (ТБ-071, ТФ-ДЕЛ-03, ТБ-077 — источник эталона должен быть из материалов
/// дела). Иначе эталоном стал бы биометрический материал чужого дела/подразделения. Вид носителя —
/// только из <see cref="ReferenceSourceKinds"/> (изображение, видео): аудиозапись лица не несёт (ADR-0026).
/// После записи эталона с лицом уже загруженные материалы дела ставятся в фоновую сверку с эталонами фигурантов
/// (ТФ-ПЕР-09, ADR-0035): материалы обычно загружают раньше, чем выбирают эталон.
/// </remarks>
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
    /// <summary>
    /// Отказ по виду носителя: источник эталона — изображение (фото или кадр видео), аудиозапись им быть
    /// не может (ADR-0026). Различим с <see cref="PersonGuard.ReferenceNotFound"/> намеренно — отказ звучит
    /// только ПОСЛЕ проверок доступа и принадлежности делу, то есть о носителе, который оператор и так видит
    /// в материалах дела; нового факта он не раскрывает (ТБ-020/021 не нарушается).
    /// </summary>
    public const string SourceKindDenied = "Эталон — изображение; аудиозапись не может быть источником эталона.";

    /// <summary>
    /// Виды носителей, пригодные источником эталона (ТБ-077): на них может быть лицо. Разрешение СПИСКОМ, а не
    /// запретом аудио: вид, добавленный в пакет «Медиа» позже, не станет источником эталона по умолчанию.
    /// </summary>
    public static readonly FrozenSet<MediaKind> ReferenceSourceKinds = FrozenSet.Create(MediaKind.Image, MediaKind.Video);

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Смена эталона — событие аудита с указанием заменяемого (ТБ-077).</remarks>
    public string? AuditSummary =>
        $"investigation:person:{PersonId}:reference-photo:add:asset={MediaAssetId};face={MediaFaceId?.ToString(CultureInfo.InvariantCulture) ?? "-"};"
        + $"supersedes={SupersedesId?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    /// <inheritdoc cref="AddReferencePhotoCommand" />
    public sealed class Handler(
        IPersonStore persons,
        IUserRoleStore roles,
        ICaseScope caseScope,
        IMediaCatalog catalog,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider,
        IPersonSuggestionScheduler suggestions)
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
            var person = await persons.GetAsync(command.PersonId, access, cancellationToken);
            if (person is null)
            {
                return ResponseDto<int>.NotFound(PersonGuard.ReferenceNotFound);
            }

            // ИНВАРИАНТ (ТБ-071, ТФ-ДЕЛ-03, ТБ-077): источник эталона — носитель из материалов ДЕЛА ФИГУРАНТА,
            // доступный субъекту (floor ядра + роль, default-deny без роли). Носитель другого дела того же
            // подразделения и с тем же грифом отвергается — иначе перебором id эталоном стал бы чужой материал.
            // Отказы неразличимы с «нет такого» (ТБ-020/021).
            if (!await caseScope.IsAssetAccessibleAsync(command.MediaAssetId, access, cancellationToken))
            {
                return ResponseDto<int>.NotFound(PersonGuard.ReferenceNotFound);
            }

            var caseAssets = await caseScope.GetAssetIdsAsync([person.CaseId], cancellationToken);
            if (!caseAssets.Contains(command.MediaAssetId))
            {
                return ResponseDto<int>.NotFound(PersonGuard.ReferenceNotFound);
            }

            // ИНВАРИАНТ (ТБ-077, ADR-0026): эталон — изображение лица; источник — только вид из списка
            // ReferenceSourceKinds. Без проверки голосовое сообщение дела (лица нет — поле лица необязательно)
            // стало бы «эталоном» фигуранта. Носитель читается каталогом пакета «Медиа» под тем же контекстом
            // доступа (решётка ТБ-020/021 на стороне БД, сужение по делам ТБ-071 уже пройдено выше); нет
            // носителя — тот же неразличимый ответ.
            var asset = await catalog.GetAsync(command.MediaAssetId, access, cancellationToken);
            if (asset is null)
            {
                return ResponseDto<int>.NotFound(PersonGuard.ReferenceNotFound);
            }

            if (!ReferenceSourceKinds.Contains(asset.Kind))
            {
                return ResponseDto<int>.BadRequest(SourceKindDenied);
            }

            // Лицо (если указано) — под контекстом доступа и именно с этого носителя: ссылка «носитель A,
            // лицо с носителя B» дала бы эталон с чужой вырезкой. Оценка качества при этом берётся с самого лица
            // (её считает конвейер «Медиа»), а не из команды: клиент не может приписать эталону чужую оценку.
            var qualityScore = command.QualityScore;
            if (command.MediaFaceId is { } faceId)
            {
                var face = await catalog.GetFaceAsync(faceId, access, cancellationToken);
                if (face is null || face.AssetId != command.MediaAssetId)
                {
                    return ResponseDto<int>.NotFound(PersonGuard.ReferenceNotFound);
                }

                qualityScore = face.QualityScore;
            }

            var draft = new ReferencePhotoDraft(
                command.PersonId, command.MediaAssetId, command.MediaFaceId, qualityScore,
                string.IsNullOrWhiteSpace(command.Source) ? null : command.Source.Trim(),
                string.IsNullOrWhiteSpace(command.LegalBasis) ? null : command.LegalBasis.Trim(),
                command.ReviewDueAt, access.NumericSubjectId);

            var (result, photoId) = await persons.AddReferencePhotoAsync(
                draft, command.SupersedesId, access, cancellationToken);
            if (result != PersonWriteResult.Ok)
            {
                return ResponseDto<int>.NotFound(PersonGuard.ReferenceNotFound);
            }

            // ТФ-ПЕР-09 (ADR-0035): материалы дела, загруженные раньше эталона, сверяются с ним в фоне. Эталон без
            // лица сверять нечем; сбой постановки эталон не отменяет (планировщик его журналирует).
            if (command.MediaFaceId is not null)
            {
                await suggestions.ScheduleCaseSweepAsync(person.CaseId, cancellationToken);
            }

            return ResponseDto<int>.Ok(photoId);
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
