using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Адаптер разрешения ссылок «справка-проверка» (<see cref="IInspectionDocumentResolver"/>):
/// документы — через порт документооборота (решётка доступа применяется ТАМ, в запросе, ТБ-020/021),
/// имена инспекторов — из реестра пользователей ядра. Application профиля на модуль не ссылается —
/// кросс-модульная склейка живёт здесь, в слое данных (как остальные адаптеры docflow-швов).
/// </summary>
public sealed class InspectionDocumentResolver(
    IDocumentLookup documentLookup,
    IAccessContextProvider accessContextProvider,
    IDbContextFactory<CoreDbContext> coreContextFactory) : IInspectionDocumentResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, InspectionDocumentCard>> ResolveAsync(
        IReadOnlyCollection<string> regNumbers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regNumbers);

        if (regNumbers.Count == 0)
        {
            return new Dictionary<string, InspectionDocumentCard>(StringComparer.Ordinal);
        }

        // Fail-closed: без контекста доступа провайдер бросает исключение, а не отдаёт «всё».
        var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
        var cards = await documentLookup.ResolveByRegNumbersAsync(regNumbers, access, cancellationToken);
        if (cards.Count == 0)
        {
            return new Dictionary<string, InspectionDocumentCard>(StringComparer.Ordinal);
        }

        // Имена инспекторов — одним запросом к ядру, склейка в памяти (ТО-инф-06).
        var inspectorIds = cards.Values
            .Where(c => c.InspectorUserId is not null)
            .Select(c => c.InspectorUserId!.Value)
            .Distinct()
            .ToList();
        var names = new Dictionary<int, string>();
        if (inspectorIds.Count > 0)
        {
            await using var core = await coreContextFactory.CreateDbContextAsync(cancellationToken);
            names = await core.Users.AsNoTracking()
                .Where(u => inspectorIds.Contains(u.Id))
                .Select(u => new { u.Id, Name = u.DisplayName ?? u.UserName })
                .ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);
        }

        return cards.ToDictionary(
            pair => pair.Key,
            pair => new InspectionDocumentCard(
                pair.Value.Id,
                pair.Value.RegNumber,
                pair.Value.RegDate,
                pair.Value.TypeName,
                pair.Value.InspectorUserId is { } inspectorId ? names.GetValueOrDefault(inspectorId) : null,
                StatusLabel(pair.Value.AggregatedStatus)),
            StringComparer.Ordinal);
    }

    // Подписи согласованы со StatusLabels модуля (DocFlow.UI недоступен из слоя данных;
    // NotApplicable — null: у справки группы «Хранение» статуса исполнения нет, чип не рисуем.
    private static string? StatusLabel(DocumentAggregatedStatus status) => status switch
    {
        DocumentAggregatedStatus.NotApplicable => null,
        DocumentAggregatedStatus.Registered => "Зарегистрирован",
        DocumentAggregatedStatus.InProgress => "В работе",
        DocumentAggregatedStatus.PartiallyDone => "Частично исполнено",
        DocumentAggregatedStatus.Done => "Исполнено",
        DocumentAggregatedStatus.Overdue => "Просрочено",
        DocumentAggregatedStatus.Closed => "Снят с контроля",
        _ => status.ToString(),
    };
}
