using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Обслуживание профиля «Следствие» при старте хоста: дозаполняет нормализованные ФИО и место жительства
/// (ТО-мат-11) у фигурантов, заведённых до появления этих полей. Без этого пересечения (ТФ-ПЕР-07) молча не
/// находили бы совпадений с прежними фигурантами, пока их карточку кто-нибудь не пересохранит.
/// </summary>
/// <remarks>
/// <para>
/// Нормализацию нельзя повторить SQL-ом в миграции: правила (<see cref="RequisiteNormalizer"/>) — код C#,
/// и расхождение SQL-копии с ним дало бы разные ключи для одного значения. Поэтому — здесь, тем же кодом.
/// </para>
/// <para>
/// Заполняются ТОЛЬКО пустые поля; уже посчитанные не трогаются. Смена правил нормализации — отдельный
/// пересчёт всех строк, а не эта служба. Работа системная, без субъекта: решётка здесь не применяется, в
/// журнал аудита не пишется (сведения не выдаются и не меняются по существу). Идёт в <see cref="StartAsync"/>
/// до приёма запросов; сбой БД на старте — только журнал, старт хоста не прерывается (как у пакета «Медиа»).
/// </para>
/// </remarks>
public sealed partial class InvestigationStartupMaintenance(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    ILogger<InvestigationStartupMaintenance> logger) : IHostedService
{
    private const int BatchSize = 500;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var filled = await BackfillNormalizedAsync(contextFactory, cancellationToken);
            if (filled > 0)
            {
                LogBackfilled(logger, filled);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogBackfillFailed(logger, exception);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Дозаполнить пустые нормализованные ФИО и место жительства; возвращает число изменённых фигурантов.
    /// Проход — по возрастанию идентификатора пачками: строка, чьё значение не даёт ключа (ФИО из одних знаков
    /// препинания), остаётся пустой и не зацикливает проход.
    /// </summary>
    public static async Task<int> BackfillNormalizedAsync(
        IDbContextFactory<InvestigationDbContext> contextFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);

        var changed = 0;
        var lastId = 0;
        while (true)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var batch = await db.Persons
                .Where(p => p.Id > lastId)
                .Where(p => (p.NameNormalized == null && !p.IsUnidentified)
                    || (p.ResidenceNormalized == null && p.Residence != null))
                .OrderBy(p => p.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return changed;
            }

            foreach (var person in batch)
            {
                var name = person.NameNormalized ?? (person.IsUnidentified ? null : RequisiteNormalizer.PersonName(person.DisplayName));
                var residence = person.ResidenceNormalized ?? RequisiteNormalizer.Address(person.Residence);
                if (name != person.NameNormalized || residence != person.ResidenceNormalized)
                {
                    person.NameNormalized = name;
                    person.ResidenceNormalized = residence;
                    changed++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            lastId = batch[^1].Id;
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Обслуживание при старте: дозаполнены нормализованные реквизиты фигурантов (ТО-мат-11): {Count}.")]
    private static partial void LogBackfilled(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Обслуживание при старте: нормализованные реквизиты фигурантов не дозаполнены — старт не прерван, повтор при следующем старте.")]
    private static partial void LogBackfillFailed(ILogger logger, Exception exception);
}
