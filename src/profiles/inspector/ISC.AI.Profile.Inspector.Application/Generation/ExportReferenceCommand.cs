using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Documents;
using ISC.AI.Profile.Inspector.Application.Common;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Generation;

using ResModel = ResponseDto<ExportReferenceResult>;

/// <summary>
/// Экспорт сформированной справки в <c>.docx</c> с обязательной маркировкой грифа (Э4-04, ТФ-ГЕН-03, ТБ-033).
/// Факт экспорта аудируется сквозным AuditBehavior (ТБ-030, Э4-11).
/// </summary>
/// <param name="Title">Заголовок документа (тема справки).</param>
/// <param name="Body">Текст справки (черновик).</param>
/// <param name="Classification">Гриф результата (наследован из генерации, =max грифов фрагментов).</param>
public sealed record ExportReferenceCommand(string Title, string Body, short Classification)
    : IRequest<ResModel>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    public string? AuditSummary => $"Экспорт справки в .docx: {Title}";

    /// <inheritdoc />
    /// <remarks>Точный гриф результата (наследован из генерации) — запись экспорта классифицируется не ниже него.</remarks>
    public short? AuditClassification => Classification;

    /// <summary>
    /// Обработчик: гриф → текстовая маркировка → рендер <c>.docx</c> с грифом в теле и метаданных →
    /// байты файла в конверте. Аудит экспорта (<see cref="AuditAction.Export"/>) пишет сквозное AuditBehavior (Э4-11).
    /// </summary>
    public sealed class Handler(IDocumentExporter exporter)
        : IRequestHandler<ExportReferenceCommand, ResModel>
    {
        private const string DocxContentType =
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        /// <inheritdoc />
        public async ValueTask<ResModel> Handle(ExportReferenceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var marking = ClassificationMarking.For(command.Classification);

            var bytes = await exporter.ExportToDocxAsync(
                new DocumentExportRequest(
                    Title: command.Title,
                    Body: command.Body,
                    ClassificationMarking: marking,
                    DraftNotice: "ЧЕРНОВИК — требует проверки человеком (HITL, ТБ-042)."),
                cancellationToken);

            return ResModel.Ok(new ExportReferenceResult(bytes, BuildFileName(command.Title), DocxContentType));
        }

        private static string BuildFileName(string title)
        {
            var cleaned = string.Concat(
                title.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' ? ch : '_')).Trim();
            return $"{(string.IsNullOrWhiteSpace(cleaned) ? "справка" : cleaned)}.docx";
        }
    }
}
