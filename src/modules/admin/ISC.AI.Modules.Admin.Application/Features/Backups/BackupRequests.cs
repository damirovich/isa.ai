using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Backup;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Backups;

/// <summary>Экран «Резервные копии» (ТФ-АДМ-04, ADR-0063): состояние ядра, имена запускавших и напоминание.</summary>
/// <param name="Overview">Настройка, носитель, оценка объёма, прогоны.</param>
/// <param name="UserNames">Имена сотрудников, запускавших копии (номер учётной записи → имя).</param>
/// <param name="LastSuccessAtUtc">Окончание последней успешной копии; <see langword="null"/> — успешных не было.</param>
/// <param name="ReminderDue">Пора сделать копию: успешных нет или последней больше <c>ReminderDays</c> дней.</param>
public sealed record BackupOverviewView(
    BackupOverview Overview,
    IReadOnlyDictionary<int, string> UserNames,
    DateTime? LastSuccessAtUtc,
    bool ReminderDue)
{
    /// <summary>Идёт копия или проверка — экран обновляется сам.</summary>
    public bool Busy => Overview.Runs.Any(r => r.Status == BackupRunStatus.Running || r.VerifyStatus == BackupVerifyStatus.Running);
}

/// <summary>
/// Состояние резервного копирования для экрана Администратора (ТФ-АДМ-04, ADR-0063). Не аудируется: в ответе нет
/// материалов — только настройка, объёмы и история прогонов, а экран во время копии перечитывает его каждые несколько
/// секунд. Аудируются действия — запуск копии и проверка (команды ниже) и их итог (ядро).
/// </summary>
public sealed record GetBackupOverviewQuery : IRequest<ResponseDto<BackupOverviewView>>
{
    /// <inheritdoc cref="GetBackupOverviewQuery" />
    public sealed class Handler(
        IBackupService backups,
        IPlatformAdministration administration,
        IUserAccountStore accounts,
        TimeProvider time)
        : IRequestHandler<GetBackupOverviewQuery, ResponseDto<BackupOverviewView>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<BackupOverviewView>> Handle(GetBackupOverviewQuery query, CancellationToken cancellationToken)
        {
            if (!await administration.CanManageBackupsAsync(cancellationToken))
            {
                return ResponseDto<BackupOverviewView>.BadRequest(AdminGuard.BackupsDenied);
            }

            var overview = await backups.GetOverviewAsync(cancellationToken);
            var ids = overview.Runs.Select(r => r.StartedByUserId).OfType<int>().ToHashSet();
            var names = ids.Count == 0
                ? new Dictionary<int, string>()
                : (await accounts.ListAsync(cancellationToken))
                    .Where(a => ids.Contains(a.UserId))
                    .ToDictionary(a => a.UserId, a => string.IsNullOrWhiteSpace(a.DisplayName) ? a.UserName : a.DisplayName!);

            var lastSuccess = overview.Runs
                .Where(r => r.Status == BackupRunStatus.Succeeded && r.FinishedAtUtc is not null)
                .Select(r => r.FinishedAtUtc)
                .Max();
            var due = lastSuccess is not { } at || time.GetUtcNow().UtcDateTime - at > TimeSpan.FromDays(overview.ReminderDays);
            return ResponseDto<BackupOverviewView>.Ok(new BackupOverviewView(overview, names, lastSuccess, due));
        }
    }
}

/// <summary>
/// «Сделать копию» (ТФ-АДМ-04, ТБ-082, ADR-0063): полная копия баз и хранилища, зашифрованная открытым ключом Заказчика,
/// на подключённый носитель — в фоне. Возвращает номер прогона.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-012/030). Право знает профиль (<see cref="IPlatformAdministration.CanManageBackupsAsync"/>). Запуск —
/// запись <c>Export</c> в журнале (данные уходят на носитель); итог копии пишет ядро отдельной записью.
/// </remarks>
public sealed record StartBackupCommand : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    public string? AuditSummary => "admin:backup:start";

    /// <inheritdoc cref="StartBackupCommand" />
    public sealed class Handler(IBackupService backups, IPlatformAdministration administration, ISubjectProvider subjectProvider)
        : IRequestHandler<StartBackupCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(StartBackupCommand command, CancellationToken cancellationToken)
        {
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId
                || !await administration.CanManageBackupsAsync(cancellationToken))
            {
                return ResponseDto<int>.BadRequest(AdminGuard.BackupsDenied);
            }

            var result = await backups.StartAsync(userId, cancellationToken);
            return result.RunId is { } runId
                ? ResponseDto<int>.Ok(runId, "Копия запущена. Не отключайте носитель до окончания.")
                : ResponseDto<int>.BadRequest(result.Refusal ?? "Копия не запущена.");
        }
    }
}

/// <summary>
/// «Проверить» (ТБ-082, ADR-0063): манифест последней готовой копии на подключённом носителе — файлы на месте, размеры и
/// SHA-256 совпадают, ключ действующий. Без закрытого ключа: расшифровка не нужна. В фоне; возвращает номер прогона.
/// </summary>
public sealed record VerifyBackupCommand : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "admin:backup:verify";

    /// <inheritdoc cref="VerifyBackupCommand" />
    public sealed class Handler(IBackupService backups, IPlatformAdministration administration, ISubjectProvider subjectProvider)
        : IRequestHandler<VerifyBackupCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(VerifyBackupCommand command, CancellationToken cancellationToken)
        {
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId
                || !await administration.CanManageBackupsAsync(cancellationToken))
            {
                return ResponseDto<int>.BadRequest(AdminGuard.BackupsDenied);
            }

            var result = await backups.VerifyLatestAsync(userId, cancellationToken);
            return result.RunId is { } runId
                ? ResponseDto<int>.Ok(runId, "Проверка запущена.")
                : ResponseDto<int>.BadRequest(result.Refusal ?? "Проверка не запущена.");
        }
    }
}
