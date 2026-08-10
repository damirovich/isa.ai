using System.Globalization;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Кеш системных настроек модуля — ОДИН на процесс.
/// </summary>
/// <remarks>
/// Отдельный singleton, а не поле хранилища: само хранилище scoped (живёт запрос), и кеш в нём
/// не пережил бы даже двух подряд обращений. Значение читают фоновая проверка сроков (каждый тик)
/// и сборка уведомлений, а меняют раз в полгода — без кеша это лишний запрос к БД на каждое событие.
/// </remarks>
public sealed class DocFlowSettingsCache
{
    private readonly Lock _gate = new();
    private DocFlowSettings? _value;

    /// <summary>Текущее закешированное значение либо <see langword="null"/>, если кеш пуст.</summary>
    public DocFlowSettings? Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    /// <summary>Кладёт значение в кеш.</summary>
    public void Set(DocFlowSettings settings)
    {
        lock (_gate)
        {
            _value = settings;
        }
    }

    /// <summary>Сбрасывает кеш — вызывается после правки настройки.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _value = null;
        }
    }
}

/// <summary>Системные настройки модуля (§9) поверх <see cref="DocFlowDbContext"/> с кешем.</summary>
public sealed class SystemSettingsStore(
    IDbContextFactory<DocFlowDbContext> contextFactory,
    DocFlowSettingsCache cache,
    IConfiguration configuration) : ISystemSettingsStore
{
    /// <summary>Горизонт по умолчанию — дефолт СКИД (разд. 5 ТЗ).</summary>
    public const int DefaultNotificationHorizonDays = 7;

    /// <inheritdoc />
    public async Task<DocFlowSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.Value is { } cached)
        {
            return cached;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var stored = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == DocFlowSettings.NotificationHorizonKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        var settings = new DocFlowSettings(ResolveHorizon(stored));
        cache.Set(settings);
        return settings;
    }

    /// <inheritdoc />
    public async Task<bool> SetNotificationHorizonAsync(
        int days, int? changedByUserId, CancellationToken cancellationToken = default)
    {
        if (days is < DocFlowSettings.MinHorizonDays or > DocFlowSettings.MaxHorizonDays)
        {
            return false;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var setting = await db.SystemSettings
            .FirstOrDefaultAsync(s => s.Key == DocFlowSettings.NotificationHorizonKey, cancellationToken);

        if (setting is null)
        {
            setting = new SystemSetting
            {
                Key = DocFlowSettings.NotificationHorizonKey,
                Value = days.ToString(CultureInfo.InvariantCulture),
            };
            db.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = days.ToString(CultureInfo.InvariantCulture);
        }

        setting.UpdatedAt = DateTime.UtcNow;
        setting.UpdatedByUserId = changedByUserId;

        await db.SaveChangesAsync(cancellationToken);

        // Сброс ПОСЛЕ успешной записи: иначе при неудачном сохранении кеш опустел бы, и следующий
        // читатель перечитал бы из БД прежнее значение — лишняя работа без изменения результата.
        cache.Invalidate();
        return true;
    }

    /// <summary>
    /// Разбирает сохранённое значение с двумя запасными вариантами.
    /// </summary>
    /// <remarks>
    /// Порядок «БД → конфигурация → 7» намеренный и обратно совместимый: до появления этой таблицы
    /// горизонт задавался ключом <c>DocFlow:NotificationHorizonDays</c>, и развёртывания, где он
    /// прописан, обязаны продолжать работать с прежним значением, пока настройку не поменяли
    /// в интерфейсе. Мусор в строке (правка руками в БД) не должен ронять фоновую задачу — он
    /// приравнивается к «значение не задано».
    /// </remarks>
    private int ResolveHorizon(string? stored)
    {
        if (int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value is >= DocFlowSettings.MinHorizonDays and <= DocFlowSettings.MaxHorizonDays)
        {
            return value;
        }

        if (int.TryParse(configuration["DocFlow:NotificationHorizonDays"], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var configured)
            && configured is >= DocFlowSettings.MinHorizonDays and <= DocFlowSettings.MaxHorizonDays)
        {
            return configured;
        }

        return DefaultNotificationHorizonDays;
    }
}
