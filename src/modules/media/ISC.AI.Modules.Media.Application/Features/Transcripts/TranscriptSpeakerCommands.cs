using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Transcripts;

/// <summary>
/// Общая проверка правок дикторов (ADR-0062): право как у повторной расшифровки (загрузка материалов), носитель под
/// решёткой и в делах субъекта (ТБ-071), известный автор правки. Недоступный носитель неотличим от несуществующего
/// (ТБ-020/021).
/// </summary>
public sealed class TranscriptSpeakerGuard(
    IMediaAdministration administration,
    IAccessContextProvider accessProvider,
    IMediaCatalog catalog,
    ICaseScope caseScope,
    ISubjectProvider subjectProvider)
{
    /// <summary>Отказ без права.</summary>
    public const string Denied = "Править дикторов расшифровки могут роли Следователь и Администратор.";

    /// <summary>Носитель не найден или недоступен.</summary>
    public const string NotFound = "Носитель не найден или недоступен.";

    /// <summary>Автор правки, если она разрешена; иначе ответ отказа.</summary>
    public async Task<(int? UserId, ResponseDto<bool>? Refusal)> CheckAsync(int assetId, CancellationToken cancellationToken)
    {
        if (!await administration.CanUploadAsync(cancellationToken)
            || await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
        {
            return (null, ResponseDto<bool>.BadRequest(Denied));
        }

        var access = await accessProvider.GetCurrentAsync(cancellationToken);
        if (await catalog.GetAsync(assetId, access, cancellationToken) is null
            || !await caseScope.IsAssetAccessibleAsync(assetId, access, cancellationToken))
        {
            return (null, ResponseDto<bool>.NotFound(NotFound));
        }

        return (userId, null);
    }

    /// <summary>Ответ по исходу правки хранилища.</summary>
    public static ResponseDto<bool> Answer(TranscriptSpeakerEditResult result, string invalidMessage) => result switch
    {
        TranscriptSpeakerEditResult.Ok => ResponseDto<bool>.Ok(true),
        TranscriptSpeakerEditResult.Invalid => ResponseDto<bool>.BadRequest(invalidMessage),
        _ => ResponseDto<bool>.NotFound("Диктор или реплика не найдены — обновите расшифровку."),
    };
}

/// <summary>
/// Переименовать диктора расшифровки («Диктор 1 → объект», ТФ-МЕД-19, ADR-0062); пустое имя возвращает «Диктор N».
/// Дословный текст не меняется (ТЭ-007). Журнал — номера носителя и диктора, без имени (имя может быть ФИО, ТБ-032).
/// </summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="Number">Номер диктора.</param>
/// <param name="Name">Новое имя; пусто — снять имя.</param>
public sealed record RenameTranscriptSpeakerCommand(int AssetId, int Number, string? Name) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <summary>Наибольшая длина имени диктора.</summary>
    public const int MaxNameLength = 100;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => string.Create(CultureInfo.InvariantCulture, $"media:transcript:asset={AssetId}:speaker={Number}:rename");

    /// <inheritdoc cref="RenameTranscriptSpeakerCommand" />
    public sealed class Handler(TranscriptSpeakerGuard guard, IMediaStore store) : IRequestHandler<RenameTranscriptSpeakerCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RenameTranscriptSpeakerCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var (userId, refusal) = await guard.CheckAsync(command.AssetId, cancellationToken);
            if (refusal is not null)
            {
                return refusal;
            }

            return TranscriptSpeakerGuard.Answer(
                await store.RenameTranscriptSpeakerAsync(command.AssetId, command.Number, command.Name, userId!.Value, cancellationToken),
                $"Имя диктора — не длиннее {MaxNameLength} символов.");
        }
    }
}

/// <summary>
/// Объединить диктора с другим (ADR-0062): реплики первого показываются репликами второго — когда модель разделила
/// одного человека на двоих. Журнал — номера, без имён.
/// </summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="Number">Кого объединить.</param>
/// <param name="Into">С кем.</param>
public sealed record MergeTranscriptSpeakersCommand(int AssetId, int Number, int Into) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => string.Create(CultureInfo.InvariantCulture, $"media:transcript:asset={AssetId}:speaker={Number}:merge-into={Into}");

    /// <inheritdoc cref="MergeTranscriptSpeakersCommand" />
    public sealed class Handler(TranscriptSpeakerGuard guard, IMediaStore store) : IRequestHandler<MergeTranscriptSpeakersCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(MergeTranscriptSpeakersCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var (userId, refusal) = await guard.CheckAsync(command.AssetId, cancellationToken);
            if (refusal is not null)
            {
                return refusal;
            }

            return TranscriptSpeakerGuard.Answer(
                await store.MergeTranscriptSpeakersAsync(command.AssetId, command.Number, command.Into, userId!.Value, cancellationToken),
                "Так объединить нельзя: диктор с самим собой или по кругу.");
        }
    }
}

/// <summary>
/// Перенести реплику к другому диктору (ADR-0062); без диктора — вернуть автоматическую разметку. Текст реплики не
/// меняется. Журнал — номера носителя, реплики и диктора.
/// </summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="SegmentIndex">Номер реплики (фрагмента).</param>
/// <param name="Speaker">Диктор; <see langword="null"/> — как разметила система.</param>
public sealed record MoveTranscriptSegmentCommand(int AssetId, int SegmentIndex, int? Speaker) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => string.Create(CultureInfo.InvariantCulture,
        $"media:transcript:asset={AssetId}:segment={SegmentIndex}:speaker={(Speaker is { } s ? s.ToString(CultureInfo.InvariantCulture) : "auto")}");

    /// <inheritdoc cref="MoveTranscriptSegmentCommand" />
    public sealed class Handler(TranscriptSpeakerGuard guard, IMediaStore store) : IRequestHandler<MoveTranscriptSegmentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(MoveTranscriptSegmentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var (userId, refusal) = await guard.CheckAsync(command.AssetId, cancellationToken);
            if (refusal is not null)
            {
                return refusal;
            }

            return TranscriptSpeakerGuard.Answer(
                await store.MoveTranscriptSegmentAsync(command.AssetId, command.SegmentIndex, command.Speaker, userId!.Value, cancellationToken),
                "Этот диктор объединён с другим — переносите реплику к тому, с кем он объединён.");
        }
    }
}

/// <summary>Правила переименования: носитель и диктор — положительные номера, имя — не длиннее 100 символов.</summary>
public sealed class RenameTranscriptSpeakerValidator : AbstractValidator<RenameTranscriptSpeakerCommand>
{
    /// <inheritdoc cref="RenameTranscriptSpeakerValidator" />
    public RenameTranscriptSpeakerValidator()
    {
        RuleFor(c => c.AssetId).GreaterThan(0);
        RuleFor(c => c.Number).GreaterThan(0);
        RuleFor(c => c.Name).MaximumLength(RenameTranscriptSpeakerCommand.MaxNameLength);
    }
}

/// <summary>Правила объединения: положительные номера, не сам с собой.</summary>
public sealed class MergeTranscriptSpeakersValidator : AbstractValidator<MergeTranscriptSpeakersCommand>
{
    /// <inheritdoc cref="MergeTranscriptSpeakersValidator" />
    public MergeTranscriptSpeakersValidator()
    {
        RuleFor(c => c.AssetId).GreaterThan(0);
        RuleFor(c => c.Number).GreaterThan(0);
        RuleFor(c => c.Into).GreaterThan(0).NotEqual(c => c.Number).WithMessage("Диктора нельзя объединить с самим собой.");
    }
}

/// <summary>Правила переноса: носитель — положительный номер, реплика — с нуля, диктор — положительный или пусто.</summary>
public sealed class MoveTranscriptSegmentValidator : AbstractValidator<MoveTranscriptSegmentCommand>
{
    /// <inheritdoc cref="MoveTranscriptSegmentValidator" />
    public MoveTranscriptSegmentValidator()
    {
        RuleFor(c => c.AssetId).GreaterThan(0);
        RuleFor(c => c.SegmentIndex).GreaterThanOrEqualTo(0);
        RuleFor(c => c.Speaker).GreaterThan(0).When(c => c.Speaker is not null);
    }
}
