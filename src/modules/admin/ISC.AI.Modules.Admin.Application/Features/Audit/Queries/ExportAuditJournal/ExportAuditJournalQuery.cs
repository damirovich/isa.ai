using System.Globalization;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Audit;

/// <summary>
/// Выгрузка журнала аудита в .csv по тому же отбору, что на экране (ТБ-030/032). Не больше
/// <see cref="AuditJournal.MaxExportRows"/> записей, новые первыми; если подходящих больше — файл помечается как
/// неполный, и экран об этом говорит.
/// </summary>
/// <param name="Filter">Отбор (страница и её размер не учитываются).</param>
/// <remarks>
/// Выгрузка — САМА аудируемое действие (<see cref="AuditAction.Export"/>): файл уносит историю за пределы системы.
/// Решётка применяется портом журнала к каждой странице — в файл не попадает ничего сверх допуска (ТБ-032).
/// </remarks>
public sealed record ExportAuditJournalQuery(AuditFilter Filter) : IRequest<ResponseDto<AuditExportFile>>, IAuditableRequest
{
    /// <summary>Размер страницы чтения: столько отдаёт порт журнала за один запрос.</summary>
    public const int ReadPageSize = 200;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    public string? AuditSummary => "core:audit:export";

    /// <inheritdoc cref="ExportAuditJournalQuery" />
    public sealed class Handler(
        IAuditReader reader,
        IAccessContextProvider accessProvider,
        IPlatformAdministration administration,
        IUserAccountStore accounts,
        IDivisionCatalog divisions,
        TimeProvider timeProvider)
        : IRequestHandler<ExportAuditJournalQuery, ResponseDto<AuditExportFile>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AuditExportFile>> Handle(ExportAuditJournalQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (!await administration.CanViewAuditAsync(cancellationToken))
            {
                return ResponseDto<AuditExportFile>.BadRequest(AdminGuard.AuditDenied);
            }

            AccessContext access;
            try
            {
                access = await accessProvider.GetCurrentAsync(cancellationToken);
            }
            catch (AccessContextRequiredException)
            {
                return ResponseDto<AuditExportFile>.BadRequest(AdminGuard.AuditClearanceRequired);
            }

            var names = await AuditJournal.LoadNamesAsync(accounts, divisions, cancellationToken);
            var rows = new List<AuditJournalRow>();
            var total = 0;
            for (var page = 1; rows.Count < AuditJournal.MaxExportRows; page++)
            {
                var chunk = await reader.QueryAsync(query.Filter with { Page = page, PageSize = ReadPageSize }, access, cancellationToken);
                total = chunk.TotalCount;
                rows.AddRange(chunk.Rows.Take(AuditJournal.MaxExportRows - rows.Count).Select(names.ToJournalRow));
                if (chunk.Rows.Count < ReadPageSize)
                {
                    break;
                }
            }

            var stamp = timeProvider.GetLocalNow().ToString("yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture);
            return ResponseDto<AuditExportFile>.Ok(new AuditExportFile(
                AuditJournal.ToCsv(rows), $"журнал-аудита_{stamp}.csv", rows.Count, total > rows.Count));
        }
    }
}
