using System.Globalization;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Reports;

/// <summary>Общие тексты, пределы и перевод исходов для сводок и справок (ТФ-ДДЛ-04/05, ADR-0031).</summary>
public static class CaseReportGuard
{
    /// <summary>Неразличимый ответ «нет или недоступно» (ТБ-020/021).</summary>
    public const string NotFound = "Документ или дело не найдены либо недоступны.";

    /// <summary>Предел длины текстового поля бланка.</summary>
    public const int MaxFieldLength = 20_000;

    /// <summary>Предел длины ячейки таблицы событий.</summary>
    public const int MaxCellLength = 4_000;

    /// <summary>Предел числа строк таблицы событий.</summary>
    public const int MaxEvents = 500;

    /// <summary>Предел длины причины правки.</summary>
    public const int MaxReasonLength = 2_000;

    /// <summary>Перевод исхода записи в конверт ответа.</summary>
    public static ResponseDto<bool> ToResponse(CaseReportWriteResult result) => result switch
    {
        CaseReportWriteResult.Ok => ResponseDto<bool>.Ok(true),
        CaseReportWriteResult.InvalidPerson => ResponseDto<bool>.BadRequest("Объект должен быть фигурантом этого дела."),
        CaseReportWriteResult.WindowClosed => ResponseDto<bool>.BadRequest(
            "Окно редактирования закрыто: документ архивный. Запросите у Администратора разрешение на правку с указанием причины (ТФ-ДДЛ-05)."),
        CaseReportWriteResult.Stale => ResponseDto<bool>.BadRequest(
            "Документ уже изменён другим сотрудником. Обновите страницу — ваша правка не сохранена, чужая не затёрта."),
        CaseReportWriteResult.Inactive => ResponseDto<bool>.BadRequest("Документ помечен неактивным и не правится."),
        CaseReportWriteResult.PermitNotNeeded => ResponseDto<bool>.BadRequest(
            "Запрос не нужен: окно редактирования ещё открыто или у вас уже есть запрос либо действующее разрешение."),
        CaseReportWriteResult.SelfDecision => ResponseDto<bool>.BadRequest("Собственный запрос решает другой Администратор (правило двух лиц)."),
        CaseReportWriteResult.AlreadyDecided => ResponseDto<bool>.BadRequest("Запрос уже решён."),
        _ => ResponseDto<bool>.NotFound(NotFound),
    };

    /// <summary>Маркировка грифа для выгрузки (ТБ-033) — по общей шкале грифов платформы (ADR-0030).</summary>
    public static string Marking(short classification) => ClassificationLevels.Marking(classification);
}

/// <summary>Правила полей бланка — общие для создания и правки.</summary>
public sealed class CaseReportContentValidator : AbstractValidator<CaseReportContent>
{
    /// <inheritdoc cref="CaseReportContentValidator" />
    public CaseReportContentValidator()
    {
        RuleFor(c => c.EventRows.Count).LessThanOrEqualTo(CaseReportGuard.MaxEvents)
            .WithMessage($"Не больше {CaseReportGuard.MaxEvents} строк событий.");
        RuleForEach(c => c.EventRows).ChildRules(e =>
        {
            e.RuleFor(x => x.Time).MaximumLength(100);
            e.RuleFor(x => x.Place).MaximumLength(CaseReportGuard.MaxCellLength);
            e.RuleFor(x => x.Description).MaximumLength(CaseReportGuard.MaxCellLength);
            e.RuleFor(x => x.Persons).MaximumLength(CaseReportGuard.MaxCellLength);
            e.RuleFor(x => x.Vehicles).MaximumLength(CaseReportGuard.MaxCellLength);
        });
        RuleFor(c => c.Identity).MaximumLength(CaseReportGuard.MaxFieldLength);
        RuleFor(c => c.Addresses).MaximumLength(CaseReportGuard.MaxFieldLength);
        RuleFor(c => c.Occupation).MaximumLength(CaseReportGuard.MaxFieldLength);
        RuleFor(c => c.Family).MaximumLength(CaseReportGuard.MaxFieldLength);
        RuleFor(c => c.Characterizing).MaximumLength(CaseReportGuard.MaxFieldLength);
        RuleFor(c => c.Conclusion).MaximumLength(CaseReportGuard.MaxFieldLength);
    }
}

/// <summary>Сводки и справки дела (ТФ-ДДЛ-04); <paramref name="Text"/> — поиск подстрокой по полям (ТО-мат-11).</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="Text">Строка поиска.</param>
public sealed record ListCaseReportsQuery(int CaseId, string? Text = null)
    : IRequest<ResponseDto<IReadOnlyList<CaseReportRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => string.IsNullOrWhiteSpace(Text) ? AuditAction.View : AuditAction.Search;

    /// <inheritdoc />
    /// <remarks>Строка поиска в сводку не идёт: в ней может быть режимный реквизит (ТБ-032).</remarks>
    public string? AuditSummary => $"investigation:case:{CaseId}:reports:list";

    /// <inheritdoc cref="ListCaseReportsQuery" />
    public sealed class Handler(ICaseReportStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<ListCaseReportsQuery, ResponseDto<IReadOnlyList<CaseReportRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CaseReportRow>>> Handle(ListCaseReportsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var rows = await store.ListByCaseAsync(query.CaseId, query.Text, access, cancellationToken);
            return rows is null
                ? ResponseDto<IReadOnlyList<CaseReportRow>>.NotFound(CaseReportGuard.NotFound)
                : ResponseDto<IReadOnlyList<CaseReportRow>>.Ok(rows, rows.Count);
        }
    }
}

/// <summary>Карточка сводки/справки: поля актуальной редакции, окно, история (ТФ-ДДЛ-05).</summary>
/// <param name="ReportId">Документ.</param>
public sealed record GetCaseReportQuery(int ReportId) : IRequest<ResponseDto<CaseReportDetails>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:report:{ReportId}:view";

    /// <inheritdoc cref="GetCaseReportQuery" />
    public sealed class Handler(ICaseReportStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<GetCaseReportQuery, ResponseDto<CaseReportDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CaseReportDetails>> Handle(GetCaseReportQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var details = await store.GetAsync(query.ReportId, access, cancellationToken);
            return details is null
                ? ResponseDto<CaseReportDetails>.NotFound(CaseReportGuard.NotFound)
                : ResponseDto<CaseReportDetails>.Ok(details);
        }
    }
}

/// <summary>Поля прежней («неактуальной») редакции из истории документа.</summary>
/// <param name="ReportId">Документ.</param>
/// <param name="Number">Номер редакции.</param>
public sealed record GetCaseReportRevisionQuery(int ReportId, int Number) : IRequest<ResponseDto<CaseReportContent>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:report:{ReportId}:revision:{Number}:view";

    /// <inheritdoc cref="GetCaseReportRevisionQuery" />
    public sealed class Handler(ICaseReportStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<GetCaseReportRevisionQuery, ResponseDto<CaseReportContent>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CaseReportContent>> Handle(GetCaseReportRevisionQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var content = await store.GetRevisionAsync(query.ReportId, query.Number, access, cancellationToken);
            return content is null
                ? ResponseDto<CaseReportContent>.NotFound(CaseReportGuard.NotFound)
                : ResponseDto<CaseReportContent>.Ok(content);
        }
    }
}

/// <summary>Новая сводка или справка по бланку (ТФ-ДДЛ-04). Заводят роли, ведущие дела (ТП-004).</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="Kind">Вид.</param>
/// <param name="ReportDate">Дата документа.</param>
/// <param name="PersonId">Объект — фигурант дела (необязательно).</param>
/// <param name="Content">Поля бланка (могут быть пустыми — заполняются позже в окне).</param>
public sealed record CreateCaseReportCommand(
    int CaseId, CaseReportKind Kind, DateOnly ReportDate, int? PersonId, CaseReportContent Content)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case:{CaseId}:report:create:{Kind}";

    /// <inheritdoc cref="CreateCaseReportCommand" />
    public sealed class Handler(
        ICaseReportStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<CreateCaseReportCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(CreateCaseReportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var (result, id) = await store.CreateAsync(
                new CaseReportDraft(command.CaseId, command.Kind, command.ReportDate, command.PersonId, command.Content),
                access, cancellationToken);
            if (result == CaseReportWriteResult.Ok)
            {
                return ResponseDto<int>.Ok(id);
            }

            var failure = CaseReportGuard.ToResponse(result);
            return failure.StatusCode == ResponseStatusCode.BadRequest
                ? ResponseDto<int>.BadRequest(failure.StatusMessage)
                : ResponseDto<int>.NotFound(CaseReportGuard.NotFound);
        }
    }
}

/// <summary>Правила создания документа.</summary>
public sealed class CreateCaseReportValidator : AbstractValidator<CreateCaseReportCommand>
{
    /// <inheritdoc cref="CreateCaseReportValidator" />
    public CreateCaseReportValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.Kind).IsInEnum();
        RuleFor(c => c.PersonId).GreaterThan(0).When(c => c.PersonId is not null);
        RuleFor(c => c.ReportDate).NotEqual(default(DateOnly)).WithMessage("Укажите дату документа.");
        RuleFor(c => c.Content).NotNull().SetValidator(new CaseReportContentValidator());
    }
}

/// <summary>
/// Сохранить поля новой редакцией (ТФ-ДДЛ-05). <paramref name="ExpectedRevision"/> — редакция, открытая на
/// экране: если документ уже изменён — отказ без затирания. После окна — только по действующему разрешению.
/// </summary>
/// <param name="ReportId">Документ.</param>
/// <param name="ExpectedRevision">Редакция, от которой шла правка.</param>
/// <param name="Content">Поля бланка.</param>
public sealed record UpdateCaseReportCommand(int ReportId, int ExpectedRevision, CaseReportContent Content)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:report:{ReportId}:update:from:{ExpectedRevision}";

    /// <inheritdoc cref="UpdateCaseReportCommand" />
    public sealed class Handler(
        ICaseReportStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<UpdateCaseReportCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(UpdateCaseReportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return CaseReportGuard.ToResponse(
                await store.UpdateAsync(command.ReportId, command.ExpectedRevision, command.Content, access, cancellationToken));
        }
    }
}

/// <summary>Правила правки документа.</summary>
public sealed class UpdateCaseReportValidator : AbstractValidator<UpdateCaseReportCommand>
{
    /// <inheritdoc cref="UpdateCaseReportValidator" />
    public UpdateCaseReportValidator()
    {
        RuleFor(c => c.ReportId).GreaterThan(0);
        RuleFor(c => c.ExpectedRevision).GreaterThan(0);
        RuleFor(c => c.Content).NotNull().SetValidator(new CaseReportContentValidator());
    }
}

/// <summary>Пометить документ неактивным или вернуть активным — удаления сводок нет (ТФ-ДДЛ-05).</summary>
/// <param name="ReportId">Документ.</param>
/// <param name="Active">Активен.</param>
public sealed record SetCaseReportActiveCommand(int ReportId, bool Active) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:report:{ReportId}:{(Active ? "activate" : "deactivate")}";

    /// <inheritdoc cref="SetCaseReportActiveCommand" />
    public sealed class Handler(
        ICaseReportStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<SetCaseReportActiveCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetCaseReportActiveCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return CaseReportGuard.ToResponse(await store.SetActiveAsync(command.ReportId, command.Active, access, cancellationToken));
        }
    }
}

/// <summary>Запросить у Администратора разрешение на правку архивного документа с причиной (ТФ-ДДЛ-05).</summary>
/// <param name="ReportId">Документ.</param>
/// <param name="Reason">Причина правки.</param>
public sealed record RequestCaseReportPermitCommand(int ReportId, string Reason) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Причину журнал получает из таблицы запросов; в сводке — только идентификатор (ТБ-032).</remarks>
    public string? AuditSummary => $"investigation:report:{ReportId}:permit:request";

    /// <inheritdoc cref="RequestCaseReportPermitCommand" />
    public sealed class Handler(
        ICaseReportStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<RequestCaseReportPermitCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RequestCaseReportPermitCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.CaseDenied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return CaseReportGuard.ToResponse(await store.RequestPermitAsync(command.ReportId, command.Reason, access, cancellationToken));
        }
    }
}

/// <summary>Причина запроса обязательна.</summary>
public sealed class RequestCaseReportPermitValidator : AbstractValidator<RequestCaseReportPermitCommand>
{
    /// <inheritdoc cref="RequestCaseReportPermitValidator" />
    public RequestCaseReportPermitValidator()
    {
        RuleFor(c => c.ReportId).GreaterThan(0);
        RuleFor(c => c.Reason).NotEmpty().WithMessage("Укажите причину правки.")
            .MaximumLength(CaseReportGuard.MaxReasonLength);
    }
}

/// <summary>
/// Очередь запросов на правку архивных документов (ТФ-АДМ-06) — только метаданные (ТБ-079). Только Администратор.
/// </summary>
/// <param name="PendingOnly">Только ждущие решения.</param>
public sealed record ListCaseReportPermitsQuery(bool PendingOnly = false)
    : IRequest<ResponseDto<IReadOnlyList<CaseReportPermitRow>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "investigation:report-permits:list";

    /// <inheritdoc cref="ListCaseReportPermitsQuery" />
    public sealed class Handler(
        ICaseReportStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<ListCaseReportPermitsQuery, ResponseDto<IReadOnlyList<CaseReportPermitRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CaseReportPermitRow>>> Handle(
            ListCaseReportPermitsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (!await RoleGuard.CallerHasRoleAsync(roles, subjectProvider, [InvestigationRole.Administrator], cancellationToken))
            {
                return ResponseDto<IReadOnlyList<CaseReportPermitRow>>.BadRequest("Очередь запросов доступна только Администратору.");
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var rows = await store.ListPermitsAsync(query.PendingOnly, access, cancellationToken);
            return ResponseDto<IReadOnlyList<CaseReportPermitRow>>.Ok(rows, rows.Count);
        }
    }
}

/// <summary>Разрешить или отказать в правке архивного документа (ТФ-АДМ-06). Только Администратор, не свой запрос.</summary>
/// <param name="PermitId">Запрос.</param>
/// <param name="Approve">Разрешить.</param>
public sealed record DecideCaseReportPermitCommand(int PermitId, bool Approve) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:report-permit:{PermitId}:{(Approve ? "approve" : "reject")}";

    /// <inheritdoc cref="DecideCaseReportPermitCommand" />
    public sealed class Handler(
        ICaseReportStore store, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<DecideCaseReportPermitCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DecideCaseReportPermitCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerHasRoleAsync(roles, subjectProvider, [InvestigationRole.Administrator], cancellationToken))
            {
                return ResponseDto<bool>.BadRequest("Решать запросы может только Администратор.");
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return CaseReportGuard.ToResponse(await store.DecidePermitAsync(command.PermitId, command.Approve, access, cancellationToken));
        }
    }
}

/// <summary>Файл выгрузки.</summary>
/// <param name="Content">Байты файла.</param>
/// <param name="FileName">Имя файла.</param>
/// <param name="ContentType">MIME-тип.</param>
public sealed record CaseReportFile(byte[] Content, string FileName, string ContentType);

/// <summary>
/// Выгрузить сводку/справку в .docx с маркировкой грифа (ТФ-ДДЛ-04, ТБ-033); <paramref name="Revision"/> — прежняя
/// редакция из истории, по умолчанию актуальная. Выгрузка аудируется как <see cref="AuditAction.Export"/>.
/// </summary>
/// <param name="ReportId">Документ.</param>
/// <param name="Revision">Номер редакции или <see langword="null"/> — актуальная.</param>
public sealed record ExportCaseReportCommand(int ReportId, int? Revision = null)
    : IRequest<ResponseDto<CaseReportFile>>, IAuditableRequest
{
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:report:{ReportId}:export:{Revision?.ToString(CultureInfo.InvariantCulture) ?? "current"}";

    /// <inheritdoc cref="ExportCaseReportCommand" />
    public sealed class Handler(
        ICaseReportStore store, ICaseReportRenderer renderer, IUserAccountStore accounts, IAccessContextProvider accessProvider)
        : IRequestHandler<ExportCaseReportCommand, ResponseDto<CaseReportFile>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CaseReportFile>> Handle(ExportCaseReportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var details = await store.GetAsync(command.ReportId, access, cancellationToken);
            if (details is null)
            {
                return ResponseDto<CaseReportFile>.NotFound(CaseReportGuard.NotFound);
            }

            var revision = command.Revision ?? details.Report.CurrentRevision;
            var content = revision == details.Report.CurrentRevision
                ? details.Content
                : await store.GetRevisionAsync(command.ReportId, revision, access, cancellationToken);
            if (content is null)
            {
                return ResponseDto<CaseReportFile>.NotFound(CaseReportGuard.NotFound);
            }

            // Исполнитель (ТБ-033 «кто изготовил») — имя текущего субъекта, иначе его идентификатор.
            var executor = access.SubjectId;
            if (access.NumericSubjectId is { } me)
            {
                var account = (await accounts.ListAsync(cancellationToken)).FirstOrDefault(a => a.UserId == me);
                if (account is not null)
                {
                    executor = string.IsNullOrWhiteSpace(account.DisplayName) ? account.UserName : account.DisplayName;
                }
            }

            var report = details.Report;
            var bytes = renderer.RenderDocx(new CaseReportExport(
                report.Kind, details.CaseNumber, report.ReportDate, report.PersonName, revision, content,
                CaseReportGuard.Marking(details.Classification), executor));

            var fileName = string.Create(CultureInfo.InvariantCulture,
                $"{report.Kind.Label()} {details.CaseNumber} {report.ReportDate:yyyy-MM-dd} ред{revision}.docx");
            fileName = string.Concat(fileName.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.' ? ch : '_'));
            return ResponseDto<CaseReportFile>.Ok(new CaseReportFile(bytes, fileName, DocxContentType));
        }
    }
}
