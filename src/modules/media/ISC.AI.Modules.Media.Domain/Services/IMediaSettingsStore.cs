using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Настройки поиска и верификации, которые ведёт Администратор (ТФ-ВЕР-07, ТФ-ПЛ-08, ТФ-АДМ-03, ADR-0054): режим
/// подтверждения, рабочий порог схожести и нижняя граница порога — в процентах шкалы <see cref="SimilarityScale"/>.
/// </summary>
/// <param name="Mode">Действующий режим подтверждения — им разбирается кандидат, по которому ещё нет решений.</param>
/// <param name="ThresholdPercent">Рабочий порог: с ним открывается поиск по лицу.</param>
/// <param name="MinThresholdPercent">
/// Нижняя граница: ниже неё оператор порог не опустит (проверка на сервере). По умолчанию 1 % — ограничения нет: оператор
/// ищет на всей шкале (решение Заказчика от 17.10.2026); Администратор может её поднять.
/// </param>
/// <param name="UpdatedAtUtc">Когда настройки меняли последний раз; <see langword="null"/> — не меняли, действуют умолчания.</param>
/// <param name="UpdatedByUserId">Кто менял последним (слабая ссылка на <c>core.app_user</c>).</param>
public sealed record MediaSettings(
    ConfirmationMode Mode,
    int ThresholdPercent,
    int MinThresholdPercent,
    DateTime? UpdatedAtUtc = null,
    int? UpdatedByUserId = null)
{
    /// <summary>
    /// Наименьший допустимый порог — 1 %: решение Заказчика от 17.10.2026 «оператор выбирает любой порог и ищет по нему»
    /// заменило пределы 40–99 % [АИС табл. 7.1 № 8] (ADR-0054 v0.2).
    /// </summary>
    public const int MinPercent = 1;

    /// <summary>Наибольший допустимый порог настроек Администратора (100 % — только полное совпадение шаблонов).</summary>
    public const int MaxPercent = 99;

    /// <summary>Режим по умолчанию — один оператор (решение Заказчика от 17.10.2026).</summary>
    public const ConfirmationMode DefaultMode = ConfirmationMode.SingleOperator;

    /// <summary>Рабочий порог по умолчанию — 70 % (порог модели по шкале; решение Заказчика от 17.10.2026).</summary>
    public const int DefaultThresholdPercent = 70;

    /// <summary>Нижняя граница по умолчанию — 1 %, то есть без ограничения (решение Заказчика от 17.10.2026).</summary>
    public const int DefaultMinThresholdPercent = MinPercent;

    /// <summary>Ключ режима в таблице <c>media.setting</c>.</summary>
    public const string ModeKey = "verification.mode";

    /// <summary>Ключ рабочего порога в таблице <c>media.setting</c>.</summary>
    public const string ThresholdKey = "search.threshold-percent";

    /// <summary>Ключ нижней границы в таблице <c>media.setting</c>.</summary>
    public const string MinThresholdKey = "search.min-threshold-percent";

    /// <summary>Запасной ключ конфигурации режима (<c>single-operator</c> / <c>two-person</c>) — пока в базе ничего нет.</summary>
    public const string ModeConfigKey = "Media:Verification:Mode";

    /// <summary>Запасной ключ конфигурации рабочего порога.</summary>
    public const string ThresholdConfigKey = "Media:Search:ThresholdPercent";

    /// <summary>Запасной ключ конфигурации нижней границы.</summary>
    public const string MinThresholdConfigKey = "Media:Search:MinThresholdPercent";

    /// <summary>Умолчания поставки: один оператор, рабочий порог 70 %, без нижней границы.</summary>
    public static MediaSettings Defaults { get; } = new(DefaultMode, DefaultThresholdPercent, DefaultMinThresholdPercent);

    /// <summary>
    /// Действующий порог поиска: запрошенный оператором, иначе рабочий; ниже границы — поднимается до границы.
    /// Граница проверяется ЗДЕСЬ, на сервере, а не только ползунком на экране (ТФ-ПЛ-08).
    /// </summary>
    public int EffectivePercent(int? requested) => Math.Max(requested ?? ThresholdPercent, MinThresholdPercent);

    /// <summary>
    /// Проверка значений Администратора: каждый порог от 1 до 99 %, рабочий не ниже границы. Нарушение — текст отказа,
    /// иначе <see langword="null"/>.
    /// </summary>
    public static string? Validate(ConfirmationMode mode, int thresholdPercent, int minThresholdPercent)
    {
        if (!Enum.IsDefined(mode))
        {
            return "Неизвестный режим подтверждения.";
        }

        if (thresholdPercent is < MinPercent or > MaxPercent || minThresholdPercent is < MinPercent or > MaxPercent)
        {
            return $"Порог схожести задаётся от {MinPercent} до {MaxPercent} %. Введите значение в этих пределах.";
        }

        return thresholdPercent < minThresholdPercent
            ? "Рабочий порог не может быть ниже нижней границы: оператор открывает поиск с рабочим порогом."
            : null;
    }

    /// <summary>Режим строкой для хранения и конфигурации.</summary>
    public static string ModeText(ConfirmationMode mode) =>
        mode == ConfirmationMode.TwoPerson ? "two-person" : "single-operator";

    /// <summary>Режим из строки хранения или конфигурации; мусор — <see langword="null"/>.</summary>
    public static ConfirmationMode? ParseMode(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "single-operator" => ConfirmationMode.SingleOperator,
        "two-person" => ConfirmationMode.TwoPerson,
        _ => null,
    };
}

/// <summary>
/// Настройки поиска и верификации пакета «Медиа» (ADR-0054): чтение с кешем и сохранение. Таблица <c>media.setting</c>,
/// порядок чтения «БД → конфигурация → умолчание».
/// </summary>
/// <remarks>
/// Кеш обязателен: режим и порог читаются при каждом поиске и каждом решении, а меняются редко. Сохранение сбрасывает
/// кеш — новое значение действует со следующего поиска или решения, без перезапуска.
/// </remarks>
public interface IMediaSettingsStore
{
    /// <summary>Действующие настройки (из кеша, при промахе — из БД).</summary>
    Task<MediaSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохраняет все три настройки одной транзакцией и сбрасывает кеш. Значения проверяются ЗДЕСЬ
    /// (<see cref="MediaSettings.Validate"/>), а не только в форме: при нарушении ничего не пишется и возвращается текст
    /// отказа; при успехе — <see langword="null"/>.
    /// </summary>
    Task<string?> SaveAsync(
        ConfirmationMode mode, int thresholdPercent, int minThresholdPercent, int? changedByUserId,
        CancellationToken cancellationToken = default);
}
