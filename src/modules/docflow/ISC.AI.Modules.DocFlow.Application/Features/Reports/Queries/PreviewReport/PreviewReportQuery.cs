using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;
using System.Globalization;

namespace ISC.AI.Modules.DocFlow.Application.Features.Reports;

/// <summary>
/// Предпросмотр отчёта на экране — до формирования файла.
/// </summary>
/// <remarks>
/// Нужен ровно затем, чтобы не выгружать вслепую: отбор задаётся семью полями, и промахнуться легко,
/// а понять это по скачанному файлу можно только открыв его. Предпросмотр НЕ пишется в журнал как
/// выгрузка (<c>Export</c>): файла не возникло, ничего из системы не ушло. Число строк ограничено —
/// на экран всё равно не поместится больше.
/// </remarks>
public sealed record PreviewReportQuery(ReportKind Kind, ReportFilter Filter, int MaxRows = 200)
    : IRequest<ResponseDto<ReportView>>
{
    /// <inheritdoc cref="PreviewReportQuery" />
    public sealed class Handler(
        IReportDataSource dataSource,
        IDivisionDirectory divisions,
        IUserDirectory users,
        IAccessContextProvider accessProvider,
        ISubjectProvider subjectProvider)
        : IRequestHandler<PreviewReportQuery, ResponseDto<ReportView>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ReportView>> Handle(
            PreviewReportQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var data = await dataSource.QueryAsync(query.Kind, query.Filter, access, cancellationToken);

            // Усечение ЧЕСТНОЕ: экран рядом пишет, сколько строк всего, чтобы «первые 200» не выдавали
            // себя за весь отчёт. Разграничение уже применено источником — здесь только показ.
            var limited = data.Rows.Count > query.MaxRows
                ? new ReportData([.. data.Rows.Take(query.MaxRows)], data.MaxClassification)
                : data;

            var subjectId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            var generatedBy = subjectId is { } id
                ? await users.GetNameAsync(id, cancellationToken) ?? "—"
                : "—";

            var names = new ReportNames(
                (await divisions.ListAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name),
                (await users.ListActiveAsync(cancellationToken)).ToDictionary(u => u.Id, u => u.Name));

            var view = ReportViewBuilder.Build(
                query.Kind, query.Filter, limited, names, generatedBy, DateTime.UtcNow);

            return ResponseDto<ReportView>.Ok(view, data.Rows.Count);
        }
    }
}
