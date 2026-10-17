using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Кеш настроек поиска и верификации — ОДИН на процесс (ADR-0054).
/// </summary>
/// <remarks>
/// Отдельный singleton, а не поле хранилища: хранилище scoped и кеш в нём не пережил бы двух обращений. Режим и порог
/// читаются при каждом поиске и каждом решении, меняются редко — без кеша это лишний запрос к БД на каждое действие.
/// </remarks>
public sealed class MediaSettingsCache
{
    private readonly Lock _gate = new();
    private MediaSettings? _value;

    /// <summary>Закешированные настройки либо <see langword="null"/>, если кеш пуст.</summary>
    public MediaSettings? Value
    {
        get
        {
            lock (_gate)
            {
                return _value;
            }
        }
    }

    /// <summary>Кладёт настройки в кеш.</summary>
    public void Set(MediaSettings settings)
    {
        lock (_gate)
        {
            _value = settings;
        }
    }

    /// <summary>Сбрасывает кеш — после сохранения настроек.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _value = null;
        }
    }
}

/// <summary>Настройки поиска и верификации (<c>media.setting</c>, ADR-0054) поверх <see cref="MediaDbContext"/> с кешем.</summary>
public sealed class MediaSettingsStore(
    IDbContextFactory<MediaDbContext> contextFactory,
    MediaSettingsCache cache,
    IConfiguration configuration) : IMediaSettingsStore
{
    private static readonly string[] Keys = [MediaSettings.ModeKey, MediaSettings.ThresholdKey, MediaSettings.MinThresholdKey];

    /// <inheritdoc />
    public async Task<MediaSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.Value is { } cached)
        {
            return cached;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Settings.AsNoTracking()
            .Where(s => Keys.Contains(s.Key))
            .ToListAsync(cancellationToken);

        var settings = Resolve(rows);
        cache.Set(settings);
        return settings;
    }

    /// <inheritdoc />
    public async Task<string?> SaveAsync(
        ConfirmationMode mode, int thresholdPercent, int minThresholdPercent, int? changedByUserId,
        CancellationToken cancellationToken = default)
    {
        if (MediaSettings.Validate(mode, thresholdPercent, minThresholdPercent) is { } error)
        {
            return error;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Settings
            .Where(s => Keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, StringComparer.Ordinal, cancellationToken);

        var now = DateTime.UtcNow;
        Put(db, rows, MediaSettings.ModeKey, MediaSettings.ModeText(mode), changedByUserId, now);
        Put(db, rows, MediaSettings.ThresholdKey, thresholdPercent.ToString(CultureInfo.InvariantCulture), changedByUserId, now);
        Put(db, rows, MediaSettings.MinThresholdKey, minThresholdPercent.ToString(CultureInfo.InvariantCulture), changedByUserId, now);

        // Три строки — одним сохранением (одна транзакция): нет состояния «режим новый, порог старый».
        await db.SaveChangesAsync(cancellationToken);

        // Сброс ПОСЛЕ успешной записи: при сбое сохранения кеш продолжает отдавать прежнее, действующее значение.
        cache.Invalidate();
        return null;
    }

    private static void Put(
        MediaDbContext db, Dictionary<string, MediaSetting> rows, string key, string value, int? changedByUserId, DateTime now)
    {
        if (!rows.TryGetValue(key, out var row))
        {
            row = new MediaSetting { Key = key, Value = value };
            db.Settings.Add(row);
        }

        row.Value = value;
        row.UpdatedAt = now;
        row.UpdatedByUserId = changedByUserId;
    }

    /// <summary>
    /// Разбор строк с запасными вариантами «БД → конфигурация → умолчание».
    /// </summary>
    /// <remarks>
    /// Мусор в строке (правка руками в БД) приравнивается к «не задано», а не роняет поиск и верификацию. Если рабочий
    /// порог оказался ниже границы (правка мимо экрана), действует граница: при сомнении — строже. Значение порога вне
    /// 40–99 % считается мусором — экран такого не сохранит.
    /// </remarks>
    private MediaSettings Resolve(List<MediaSetting> rows)
    {
        string? Stored(string key) => rows.FirstOrDefault(r => r.Key == key)?.Value;

        var mode = MediaSettings.ParseMode(Stored(MediaSettings.ModeKey))
            ?? MediaSettings.ParseMode(configuration[MediaSettings.ModeConfigKey])
            ?? MediaSettings.DefaultMode;
        var min = Percent(Stored(MediaSettings.MinThresholdKey))
            ?? Percent(configuration[MediaSettings.MinThresholdConfigKey])
            ?? MediaSettings.DefaultMinThresholdPercent;
        var threshold = Percent(Stored(MediaSettings.ThresholdKey))
            ?? Percent(configuration[MediaSettings.ThresholdConfigKey])
            ?? MediaSettings.DefaultThresholdPercent;

        var latest = rows.Count == 0 ? null : rows.MaxBy(r => r.UpdatedAt);
        return new MediaSettings(
            mode, Math.Max(threshold, min), min,
            latest is null ? null : DateTime.SpecifyKind(latest.UpdatedAt, DateTimeKind.Utc),
            latest?.UpdatedByUserId);
    }

    private static int? Percent(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
        && value is >= MediaSettings.MinPercent and <= MediaSettings.MaxPercent
            ? value
            : null;
}
