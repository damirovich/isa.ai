using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Documents;

/// <summary>
/// Сценарии комментариев к документу (ТЗ СКИД §4.8). КАЖДЫЙ сценарий сперва проверяет допуск к САМОМУ
/// документу через <c>IDocumentStore.GetAsync</c> (решётка гриф/подразделение + построчная политика роли,
/// этапы 6.1/6.4): комментарии к недоступному документу неотличимы от «документа нет» — иначе лента
/// комментариев стала бы обходным каналом чтения закрытого документа.
/// </summary>

/// <summary>Лента комментариев документа (§4.8).</summary>
/// <param name="IncludeResolved">
/// Показывать закрытые обсуждения. Отсев выполняет хранилище — В ЗАПРОСЕ, а не в разметке.
/// </param>
public sealed record ListCommentsQuery(int DocumentId, bool IncludeResolved = true)
    : IRequest<ResponseDto<IReadOnlyList<CommentItem>>>
{
    /// <inheritdoc cref="ListCommentsQuery" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider)
        : IRequestHandler<ListCommentsQuery, ResponseDto<IReadOnlyList<CommentItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CommentItem>>> Handle(
            ListCommentsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (await documents.GetAsync(query.DocumentId, access, cancellationToken) is null)
            {
                return ResponseDto<IReadOnlyList<CommentItem>>.NotFound("Документ не найден.");
            }

            var items = await comments.ListAsync(
                query.DocumentId, query.IncludeResolved, cancellationToken);
            return ResponseDto<IReadOnlyList<CommentItem>>.Ok(items, items.Count);
        }
    }
}

/// <summary>Добавить комментарий или ответ (§4.8).</summary>
public sealed record AddCommentCommand(
    int DocumentId,
    int? ParentCommentId,
    string Content,
    CommentType CommentType,
    IReadOnlyList<int> MentionedUserIds,
    IReadOnlyList<UploadedFile>? Files = null) : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Текст комментария в журнал НЕ пишется — только факт и объект (ТБ-032).</remarks>
    public string? AuditSummary => $"docflow:document:{DocumentId}:comment:add";

    /// <inheritdoc cref="AddCommentCommand" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider,
        DocFlowEventNotifier notifier)
        : IRequestHandler<AddCommentCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(AddCommentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } authorId)
            {
                return ResponseDto<int>.BadRequest("Комментарий требует аутентифицированного пользователя.");
            }

            var document = await documents.GetAsync(command.DocumentId, access, cancellationToken);
            if (document is null)
            {
                return ResponseDto<int>.NotFound("Документ не найден.");
            }

            var (status, commentId) = await comments.AddAsync(
                new CommentDraft(
                    command.DocumentId, command.ParentCommentId, command.Content, command.CommentType,
                    command.MentionedUserIds, command.Files),
                authorId, cancellationToken);

            if (status == CommentWriteStatus.Ok)
            {
                // Разд. 5: упомянутым — «вас упомянули», остальным участникам — «добавлен комментарий».
                // Сбой уведомления не отменяет уже сохранённый комментарий (см. DocFlowEventNotifier).
                await DocFlowEventNotifier.SafeAsync(() => notifier.CommentAddedAsync(
                    document, commentId, authorId, command.MentionedUserIds, cancellationToken));
            }

            return status switch
            {
                CommentWriteStatus.Ok => ResponseDto<int>.Ok(commentId),
                CommentWriteStatus.NotFound => ResponseDto<int>.NotFound("Документ или комментарий не найден."),
                CommentWriteStatus.ParentMismatch =>
                    ResponseDto<int>.BadRequest("Ответ относится к комментарию другого документа."),
                _ => ResponseDto<int>.Fail("Не удалось добавить комментарий."),
            };
        }
    }
}

/// <summary>Изменить свой комментарий (§4.8): правится только текст и упоминания.</summary>
public sealed record UpdateCommentCommand(
    int DocumentId, int CommentId, string Content, IReadOnlyList<int> MentionedUserIds)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:comment:{CommentId}:edit";

    /// <inheritdoc cref="UpdateCommentCommand" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider)
        : IRequestHandler<UpdateCommentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UpdateCommentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } editorId)
            {
                return ResponseDto<bool>.BadRequest("Правка комментария требует аутентифицированного пользователя.");
            }

            if (await documents.GetAsync(command.DocumentId, access, cancellationToken) is null)
            {
                return ResponseDto<bool>.NotFound("Документ не найден.");
            }

            var status = await comments.UpdateAsync(
                command.CommentId, command.Content, command.MentionedUserIds, editorId, cancellationToken);
            return CommentResponses.ToResponse(status);
        }
    }
}

/// <summary>Удалить свой комментарий (§4.8): мягко, содержимое перестаёт отдаваться.</summary>
public sealed record DeleteCommentCommand(int DocumentId, int CommentId)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:comment:{CommentId}:delete";

    /// <inheritdoc cref="DeleteCommentCommand" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider)
        : IRequestHandler<DeleteCommentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            DeleteCommentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } actorId)
            {
                return ResponseDto<bool>.BadRequest("Удаление комментария требует аутентифицированного пользователя.");
            }

            if (await documents.GetAsync(command.DocumentId, access, cancellationToken) is null)
            {
                return ResponseDto<bool>.NotFound("Документ не найден.");
            }

            var status = await comments.DeleteAsync(command.CommentId, actorId, cancellationToken);
            return CommentResponses.ToResponse(status);
        }
    }
}

/// <summary>Закрыть/переоткрыть обсуждение (§4.8) — только корневой комментарий.</summary>
public sealed record SetCommentResolvedCommand(int DocumentId, int CommentId, bool Resolved)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:comment:{CommentId}:{(Resolved ? "resolve" : "unresolve")}";

    /// <inheritdoc cref="SetCommentResolvedCommand" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider)
        : IRequestHandler<SetCommentResolvedCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetCommentResolvedCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } actorId)
            {
                return ResponseDto<bool>.BadRequest("Действие требует аутентифицированного пользователя.");
            }

            if (await documents.GetAsync(command.DocumentId, access, cancellationToken) is null)
            {
                return ResponseDto<bool>.NotFound("Документ не найден.");
            }

            var status = await comments.SetResolvedAsync(
                command.CommentId, command.Resolved, actorId, cancellationToken);
            return CommentResponses.ToResponse(status);
        }
    }
}

/// <summary>Единое сопоставление статуса записи комментария с ответом (тексты — в одном месте).</summary>
internal static class CommentResponses
{
    public static ResponseDto<bool> ToResponse(CommentWriteStatus status) => status switch
    {
        CommentWriteStatus.Ok => ResponseDto<bool>.Ok(true),
        CommentWriteStatus.NotFound => ResponseDto<bool>.NotFound("Комментарий не найден."),
        CommentWriteStatus.NotAuthor => ResponseDto<bool>.BadRequest("Изменять и удалять можно только свой комментарий."),
        CommentWriteStatus.AlreadyDeleted => ResponseDto<bool>.BadRequest("Комментарий удалён — правка невозможна."),
        CommentWriteStatus.OnlyRootCanBeResolved =>
            ResponseDto<bool>.BadRequest("Закрыть можно только обсуждение целиком, не отдельный ответ (ТЗ §4.8)."),
        CommentWriteStatus.Conflict =>
            ResponseDto<bool>.BadRequest("Комментарий изменён параллельно — обновите страницу и повторите."),
        _ => ResponseDto<bool>.Fail("Не удалось выполнить действие с комментарием."),
    };
}

/// <summary>
/// Правила формы комментария (§4.8). Лимиты файлов НАМЕРЕННО свои, НЕ <c>FileRules</c>: у комментариев
/// в СКИД собственный набор — 10 МБ на файл (не 25), ≤5 файлов (не 10), картинки РАЗРЕШЕНЫ (в отличие
/// от файлов документа/переходов), суммарного лимита нет. Переиспользование <c>FileRules</c> здесь молча
/// подменило бы контракт.
/// </summary>
public sealed class AddCommentValidator : AbstractValidator<AddCommentCommand>
{
    /// <summary>Максимум файлов на комментарий (СКИД §4.8).</summary>
    public const int MaxFiles = 5;

    /// <summary>Максимальный размер файла комментария — 10 МБ (СКИД §4.8, их SAD §5.3).</summary>
    public const long MaxFileBytes = 10L * 1024 * 1024;

    /// <summary>Максимальная длина текста комментария.</summary>
    public const int MaxContentLength = 10_000;

    /// <summary>Разрешённые типы файлов комментария — включая изображения (в отличие от файлов документа).</summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "image/png",
        "image/jpeg",
    };

    /// <summary>Правила добавления комментария.</summary>
    public AddCommentValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.Content).NotEmpty().WithMessage("Комментарий не может быть пустым.")
            .MaximumLength(MaxContentLength);
        RuleFor(c => c.CommentType).IsInEnum();
        RuleFor(c => c.Files)
            .Must(files => files is null || files.Count <= MaxFiles)
                .WithMessage($"Не больше {MaxFiles} файлов на комментарий.")
            .Must(files => files is null || files.All(f => f.Content.LongLength is > 0 and <= MaxFileBytes))
                .WithMessage("Файл пуст или больше 10 МБ.")
            .Must(files => files is null || files.All(f => AllowedContentTypes.Contains(f.ContentType)))
                .WithMessage("Допустимые форматы файлов комментария: PDF, DOCX, PNG, JPEG.");
    }
}

/// <inheritdoc cref="AddCommentValidator" />
public sealed class UpdateCommentValidator : AbstractValidator<UpdateCommentCommand>
{
    /// <summary>Правила правки: только текст (файлы и вид комментария неизменяемы — как в СКИД).</summary>
    public UpdateCommentValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.CommentId).GreaterThan(0);
        RuleFor(c => c.Content).NotEmpty().WithMessage("Комментарий не может быть пустым.")
            .MaximumLength(AddCommentValidator.MaxContentLength);
    }
}
