using System.Globalization;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Documents;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Application.Features.Reports;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.CaseDocuments;

/// <summary>Документ дела для вкладки «Документы» (ТФ-ДЕЛ-02): карточка документооборота и сведения о привязке.</summary>
/// <param name="Document">Краткая карточка документа.</param>
/// <param name="LinkedByUserId">Кто прикрепил.</param>
/// <param name="LinkedAt">Когда прикреплён (UTC).</param>
public sealed record CaseDocumentRow(DocumentBrief Document, int? LinkedByUserId, DateTime LinkedAt);

/// <summary>Общие тексты документов дела.</summary>
public static class CaseDocumentGuard
{
    /// <summary>Неразличимый ответ: дело или документ не найдены либо недоступны (ТБ-021).</summary>
    public const string NotFound = "Дело или документ не найдены либо недоступны.";

    /// <summary>Перевод исхода записи в ответ.</summary>
    public static ResponseDto<bool> ToResponse(CaseDocumentWriteResult result) => result switch
    {
        CaseDocumentWriteResult.Ok => ResponseDto<bool>.Ok(true),
        CaseDocumentWriteResult.AlreadyLinked => ResponseDto<bool>.BadRequest("Документ уже прикреплён к этому делу."),
        CaseDocumentWriteResult.NotLinked => ResponseDto<bool>.BadRequest("Документ не прикреплён к этому делу."),
        _ => ResponseDto<bool>.NotFound(NotFound),
    };
}

/// <summary>
/// Документы дела (ТФ-ДЕЛ-02): привязки дела с карточками документооборота. Документ, недоступный субъекту по
/// правилам документооборота, в выдачу не попадает — ни строкой, ни счётчиком (ТБ-021).
/// </summary>
/// <param name="CaseId">Дело.</param>
public sealed record ListCaseDocumentsQuery(int CaseId) : IRequest<ResponseDto<IReadOnlyList<CaseDocumentRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case:{CaseId}:documents:list";

    /// <inheritdoc cref="ListCaseDocumentsQuery" />
    public sealed class Handler(ICaseDocumentStore store, IDocumentLookup documents, IAccessContextProvider accessProvider)
        : IRequestHandler<ListCaseDocumentsQuery, ResponseDto<IReadOnlyList<CaseDocumentRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CaseDocumentRow>>> Handle(ListCaseDocumentsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var links = await store.ListAsync(query.CaseId, access, cancellationToken);
            if (links is null)
            {
                return ResponseDto<IReadOnlyList<CaseDocumentRow>>.NotFound(CaseDocumentGuard.NotFound);
            }

            // Карточки — одним вызовом порта под решёткой документооборота; недоступные в словаре отсутствуют.
            var cards = await documents.ResolveByIdsAsync(links.Select(l => l.DocumentId).ToList(), access, cancellationToken);
            var rows = links
                .Where(l => cards.ContainsKey(l.DocumentId))
                .Select(l => new CaseDocumentRow(cards[l.DocumentId], l.LinkedByUserId, l.LinkedAt))
                .ToList();
            return ResponseDto<IReadOnlyList<CaseDocumentRow>>.Ok(rows, rows.Count);
        }
    }
}

/// <summary>
/// Прикрепить к делу документ документооборота (ТФ-ДЕЛ-02). Прикрепляют роли, ведущие дела (ТП-004); документ
/// должен быть виден субъекту в документообороте — иначе «не найден» (прикреплением нельзя прощупать документы).
/// </summary>
/// <param name="CaseId">Дело.</param>
/// <param name="DocumentId">Документ.</param>
public sealed record AttachCaseDocumentCommand(int CaseId, int DocumentId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case:{CaseId}:document:{DocumentId}:attach";

    /// <inheritdoc cref="AttachCaseDocumentCommand" />
    public sealed class Handler(
        ICaseDocumentStore store, IDocumentLookup documents, IUserRoleStore roles, ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider)
        : IRequestHandler<AttachCaseDocumentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(AttachCaseDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var visible = await documents.ResolveByIdsAsync([command.DocumentId], access, cancellationToken);
            if (!visible.ContainsKey(command.DocumentId))
            {
                return ResponseDto<bool>.NotFound(CaseDocumentGuard.NotFound);
            }

            return CaseDocumentGuard.ToResponse(await store.AttachAsync(command.CaseId, command.DocumentId, access, cancellationToken));
        }
    }
}

/// <summary>Открепить документ от дела (ошибочное прикрепление); сам документ в документообороте остаётся.</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="DocumentId">Документ.</param>
public sealed record DetachCaseDocumentCommand(int CaseId, int DocumentId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case:{CaseId}:document:{DocumentId}:detach";

    /// <inheritdoc cref="DetachCaseDocumentCommand" />
    public sealed class Handler(
        ICaseDocumentStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<DetachCaseDocumentCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DetachCaseDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return CaseDocumentGuard.ToResponse(await store.DetachAsync(command.CaseId, command.DocumentId, access, cancellationToken));
        }
    }
}

/// <summary>
/// Зарегистрировать выгрузку сводки/справки в документообороте и прикрепить к делу (ТФ-ДДЛ-04, ТФ-ДДЛ-01): .docx
/// актуальной редакции → новый документ (автономер, вид — <paramref name="TypeId"/>, внутренний, гриф и
/// подразделение — с дела) → файл документа → привязка к делу. Каждый шаг — сценарий своего модуля со своим
/// аудитом и правилами; этот сценарий их только связывает.
/// </summary>
/// <param name="ReportId">Сводка или справка.</param>
/// <param name="TypeId">Вид документа в документообороте.</param>
public sealed record RegisterCaseReportDocumentCommand(int ReportId, int TypeId) : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:report:{ReportId}:register:type:{TypeId}";

    /// <inheritdoc cref="RegisterCaseReportDocumentCommand" />
    public sealed class Handler(
        IMediator mediator,
        ICaseReportStore reports,
        ICaseStore cases,
        ICaseDocumentStore links,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider)
        : IRequestHandler<RegisterCaseReportDocumentCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(RegisterCaseReportDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var details = await reports.GetAsync(command.ReportId, access, cancellationToken);
            var caseDetails = details is null ? null : await cases.GetAsync(details.Report.CaseId, access, cancellationToken);
            if (details is null || caseDetails is null)
            {
                return ResponseDto<int>.NotFound(CaseReportGuard.NotFound);
            }

            var file = await mediator.Send(new ExportCaseReportCommand(command.ReportId), cancellationToken);
            if (file is not { Status: true, Data: { } docx })
            {
                return ResponseDto<int>.BadRequest(file.StatusMessage);
            }

            var report = details.Report;
            var summary = string.Create(CultureInfo.InvariantCulture,
                $"{report.Kind.Label()} за {report.ReportDate:dd.MM.yyyy} · дело № {details.CaseNumber}{(report.PersonName is { } name ? $" · объект: {name}" : string.Empty)} · редакция № {report.CurrentRevision}");

            var registered = await mediator.Send(new RegisterDocumentCommand(
                RegNumber: null,
                RegDate: DateOnly.FromDateTime(DateTime.Now),
                TypeId: command.TypeId,
                Direction: DocumentDirection.Internal,
                Source: null,
                ShortContent: summary,
                FullText: null,
                Notes: null,
                Priority: null,
                InspectorUserId: access.NumericSubjectId,
                Classification: details.Classification,
                DivisionId: caseDetails.DivisionId,
                Assignments: [],
                UseCommonDeadline: false,
                CommonDeadline: null), cancellationToken);
            if (registered is not { Status: true, Data: var documentId })
            {
                return ResponseDto<int>.BadRequest(registered.StatusMessage);
            }

            // Документ уже зарегистрирован: сбой файла или привязки не откатывает регистрацию — говорим, что именно
            // не сделано, чтобы сотрудник доделал руками, а не зарегистрировал второй раз.
            var uploaded = await mediator.Send(new UploadDocumentFileCommand(
                documentId, docx.FileName, docx.ContentType, docx.Content, DocumentLanguage.Russian), cancellationToken);
            if (!uploaded.Status)
            {
                return ResponseDto<int>.BadRequest($"Документ № {documentId} зарегистрирован, но файл не прикреплён: {uploaded.StatusMessage}");
            }

            var linked = await links.AttachAsync(report.CaseId, documentId, access, cancellationToken);
            return linked is CaseDocumentWriteResult.Ok or CaseDocumentWriteResult.AlreadyLinked
                ? ResponseDto<int>.Ok(documentId)
                : ResponseDto<int>.BadRequest($"Документ № {documentId} зарегистрирован, но не прикреплён к делу — прикрепите его на вкладке «Документы».");
        }
    }
}
