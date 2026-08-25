using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Inspector.Application.Features.Loading;

using ResModel = ResponseDto<BundleImportResult>;

/// <summary>
/// Импорт пакета сборщика (Harvester, Э4-08↔Э4-01): по пути к <c>manifest.json</c> загружает документы
/// в корпус через <see cref="IBundleImporter"/> (на каждый — fail-closed гриф, дедуп).
/// </summary>
/// <remarks>
/// Подразделение выбирает ОПЕРАТОР внутри контура из справочника и оно ПЕРЕКРЫВАЕТ записанное в пакете:
/// сборщик работает вне контура и справочника не видит — номер в пакете лишь намерение, а не факт.
/// Подразделение-призрак (нет в справочнике) отклоняется ДО чтения пакета (<see cref="DivisionRule"/>).
/// </remarks>
/// <param name="ManifestPath">Путь к манифесту пакета.</param>
/// <param name="DivisionId">Подразделение из справочника, под которым ложится весь пакет.</param>
/// <param name="Classification">
/// Гриф, подтверждённый оператором для всего пакета (ТБ-024): перекрывает записанный в пакете —
/// пакет собран вне контура недоверенным производителем, гриф там лишь намерение.
/// </param>
public sealed record ImportBundleCommand(string ManifestPath, int DivisionId, short Classification)
    : IRequest<ResModel>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Ingest;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"Импорт пакета в корпус: {ManifestPath}; подразделение={DivisionId}; гриф={Classification}";

    /// <summary>Обработчик: проверяет подразделение и манифест, запускает импорт.</summary>
    public sealed class Handler(IBundleImporter importer, IDivisionAdminStore divisions, IBackgroundTaskQueue taskQueue)
        : IRequestHandler<ImportBundleCommand, ResModel>
    {
        /// <inheritdoc />
        public async ValueTask<ResModel> Handle(ImportBundleCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await DivisionRule.ExistsAsync(divisions, command.DivisionId, cancellationToken))
            {
                return ResModel.BadRequest(DivisionRule.Missing(command.DivisionId));
            }

            if (!File.Exists(command.ManifestPath))
            {
                return ResModel.NotFound($"Манифест не найден: {command.ManifestPath}");
            }

            var result = await importer.ImportAsync(
                command.ManifestPath, command.DivisionId, command.Classification, cancellationToken);

            // Картотека достраивается ФОНОМ после импорта (нормы/редакции/связки из метаданных ЦБД):
            // оператор не ждёт обход корпуса; синхронизация идемпотентна, повтор безопасен.
            if (result.Imported > 0)
            {
                await taskQueue.EnqueueAsync(
                    "Синхронизация картотеки НПА с корпусом",
                    async (sp, ct) => await sp.GetRequiredService<INpaRegistrySynchronizer>().SyncFromCorpusAsync(ct),
                    cancellationToken);
            }

            return ResModel.Ok(result,
                $"Импортировано: {result.Imported}, дублей: {result.Duplicates}, отклонено: {result.Rejected} (всего {result.Total}).");
        }
    }
}
