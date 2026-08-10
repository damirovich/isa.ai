using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Ingestion;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Loading;

using ResModel = ResponseDto<BundleImportResult>;

/// <summary>
/// Импорт пакета сборщика (Harvester, Э4-08↔Э4-01): по пути к <c>manifest.json</c> загружает документы
/// в корпус через <see cref="IBundleImporter"/> (на каждый — fail-closed гриф, дедуп).
/// </summary>
/// <param name="ManifestPath">Путь к манифесту пакета.</param>
public sealed record ImportBundleCommand(string ManifestPath) : IRequest<ResModel>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Ingest;

    /// <inheritdoc />
    public string? AuditSummary => $"Импорт пакета в корпус: {ManifestPath}";

    /// <summary>Обработчик: проверяет наличие манифеста и запускает импорт.</summary>
    public sealed class Handler(IBundleImporter importer) : IRequestHandler<ImportBundleCommand, ResModel>
    {
        /// <inheritdoc />
        public async ValueTask<ResModel> Handle(ImportBundleCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!File.Exists(command.ManifestPath))
            {
                return ResModel.NotFound($"Манифест не найден: {command.ManifestPath}");
            }

            var result = await importer.ImportAsync(command.ManifestPath, cancellationToken);
            return ResModel.Ok(result,
                $"Импортировано: {result.Imported}, дублей: {result.Duplicates}, отклонено: {result.Rejected} (всего {result.Total}).");
        }
    }
}
