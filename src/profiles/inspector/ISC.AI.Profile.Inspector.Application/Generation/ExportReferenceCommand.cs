using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Documents;
using ISC.AI.Profile.Inspector.Application.Common;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Generation;

using ResModel = ResponseDto<ExportReferenceResult>;

/// <summary>
/// Экспорт сформированной справки в <c>.docx</c> с обязательной маркировкой грифа (Э4-04, ТФ-ГЕН-03, ТБ-033).
/// Факт экспорта аудируется (ТБ-030).
/// </summary>
/// <param name="Title">Заголовок документа (тема справки).</param>
/// <param name="Body">Текст справки (черновик).</param>
/// <param name="Classification">Гриф результата (наследован из генерации, =max грифов фрагментов).</param>
public sealed record ExportReferenceCommand(string Title, string Body, short Classification) : IRequest<ResModel>
{
    /// <summary>
    /// Обработчик: гриф → текстовая маркировка → рендер <c>.docx</c> с грифом в теле и метаданных →
    /// аудит <see cref="AuditAction.Export"/> → байты файла в конверте.
    /// </summary>
    public sealed class Handler(IDocumentExporter exporter, IAuditWriter auditWriter)
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

            // Аудит экспорта (ТБ-030): гриф = гриф результата; содержимое — отдельно (ТБ-032).
            // Субъект (SubjectId) проставится с появлением внешней аутентификации (Э3-08).
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Export,
                    command.Classification,
                    PayloadSensitive: $"Экспорт справки в .docx: {command.Title}"),
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
