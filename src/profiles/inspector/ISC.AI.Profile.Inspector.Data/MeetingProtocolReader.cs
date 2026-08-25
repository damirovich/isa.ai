using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Читатель протокола совещания (<see cref="IMeetingProtocolReader"/>): документ с назначениями —
/// через хранилище документооборота (решётка доступа применяется ТАМ, ТБ-020/021), имена
/// подразделений — из справочника профиля одним запросом (склейка в памяти, ТО-инф-06).
/// </summary>
public sealed class MeetingProtocolReader(
    IDocumentStore documentStore,
    IAccessContextProvider accessContextProvider,
    IDbContextFactory<InspectorDbContext> contextFactory) : IMeetingProtocolReader
{
    /// <inheritdoc />
    public async Task<MeetingProtocol?> ReadAsync(int documentId, CancellationToken cancellationToken = default)
    {
        // Fail-closed: без контекста доступа провайдер бросает исключение, чтения не будет.
        var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
        var document = await documentStore.GetAsync(documentId, access, cancellationToken);
        if (document is null)
        {
            return null;
        }

        var divisionIds = document.Assignments.Select(a => a.DivisionId).Distinct().ToList();
        var names = new Dictionary<int, string>();
        if (divisionIds.Count > 0)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            names = await db.Divisions.AsNoTracking()
                .Where(d => divisionIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        }

        return new MeetingProtocol(
            document.Id,
            document.RegNumber,
            document.RegDate,
            document.ShortContent,
            document.Classification,
            [.. document.Assignments.Select(a => new MeetingItem(
                a.Id,
                names.GetValueOrDefault(a.DivisionId, $"подразделение №{a.DivisionId}"),
                a.Deadline,
                StatusLabel(a.Status),
                IsDone: a.Status is AssignmentStatus.Done or AssignmentStatus.Closed,
                IsOverdue: a.Status == AssignmentStatus.Overdue))]);
    }

    // Подписи согласованы со StatusLabels модуля (DocFlow.UI недоступен из слоя данных).
    private static string StatusLabel(AssignmentStatus status) => status switch
    {
        AssignmentStatus.Registered => "Зарегистрировано",
        AssignmentStatus.InControl => "Контроль",
        AssignmentStatus.InProgress => "В работе",
        AssignmentStatus.PartiallyDone => "Частично исполнено",
        AssignmentStatus.Done => "Исполнено",
        AssignmentStatus.Overdue => "Просрочено",
        AssignmentStatus.Closed => "Снято с контроля",
        _ => status.ToString(),
    };
}
