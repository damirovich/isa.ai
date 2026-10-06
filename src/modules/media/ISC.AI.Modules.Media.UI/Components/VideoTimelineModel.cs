using System;
using System.Collections.Generic;
using System.Globalization;

namespace ISC.AI.Modules.Media.UI;

/// <summary>Отрезок на дорожке ленты видео (ADR-0038): начало, конец (равен началу — точка) и подсказка.</summary>
/// <param name="StartMs">Начало, мс.</param>
/// <param name="EndMs">Конец, мс.</param>
/// <param name="Tooltip">Подсказка при наведении.</param>
public sealed record TimelineSpan(long StartMs, long EndMs, string Tooltip);


/// <summary>
/// Дорожка отметок под лентой кадров (ADR-0038): подтверждённый фигурант — своим цветом, имя внутри отметки и в легенде.
/// </summary>
/// <param name="Key">Ключ дорожки (для отрисовки списком).</param>
/// <param name="Title">Подпись: имя фигуранта.</param>
/// <param name="Color">Цвет отметок (#rrggbb).</param>
/// <param name="Spans">Отрезки по времени.</param>
public sealed record TimelineLane(string Key, string Title, string Color, IReadOnlyList<TimelineSpan> Spans);

/// <summary>Деление шкалы: момент и подпись.</summary>
/// <param name="Ms">Момент, мс.</param>
/// <param name="Label">Подпись «мм:сс» (от часа — «ч:мм:сс»); пустая — подпись у правого края не помещается.</param>
public sealed record TimelineTick(long Ms, string Label);

/// <summary>
/// Опора часов записи для ленты (ТФ-МЕД-11): момент начала записи как «настенное» время пояса сервера, в мс от эпохи
/// (без пересчёта браузером — так ленту и реквизиты на странице видят одинаково), и откуда оно взято.
/// </summary>
/// <param name="WallClockEpochMs">Начало записи — местное время сервера, записанное как UTC, мс от 1970-01-01.</param>
/// <param name="Approximate">
/// Опора — дата съёмки из реквизитов (оператор указывает её с точностью до минуты), а не метаданные файла:
/// время показывается со знаком «≈» и без миллисекунд.
/// </param>
public sealed record RecordClock(long WallClockEpochMs, bool Approximate);

/// <summary>
/// Расчёты ленты под видео (ADR-0038), не зависящие от браузера: деления шкалы, проценты положения, цвета дорожек
/// фигурантов и опора часов записи. Положение на ленте — доля длительности записи, поэтому отметки и деления
/// рисуются процентами и держатся при любой ширине экрана.
/// </summary>
public static class VideoTimelineModel
{
    /// <summary>Делений на шкале — не больше этого: подписи не налезают друг на друга.</summary>
    public const int MaxTicks = 10;

    /// <summary>Правее этой доли ширины подпись деления не ставится: она обрезалась бы краем ленты.</summary>
    public const double LastLabelPercent = 94;

    // «Круглые» шаги шкалы, с: секунды, минуты, часы.
    private static readonly long[] TickStepsSeconds = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400];

    // Цвета дорожек фигурантов: различимы между собой и на светлой, и на тёмной теме.
    private static readonly string[] PersonColors =
        ["#e53935", "#1e88e5", "#43a047", "#fb8c00", "#8e24aa", "#00acc1", "#d81b60", "#6d4c41"];

    /// <summary>Шаг делений, мс: наименьший «круглый» шаг, при котором делений не больше <see cref="MaxTicks"/>.</summary>
    public static long TickStepMs(long durationMs)
    {
        foreach (var seconds in TickStepsSeconds)
        {
            var step = seconds * 1000;
            if (durationMs / step < MaxTicks)
            {
                return step;
            }
        }

        return TickStepsSeconds[^1] * 1000;
    }

    /// <summary>Деления шкалы от нуля до конца записи с подписями «мм:сс».</summary>
    public static IReadOnlyList<TimelineTick> Ticks(long durationMs)
    {
        if (durationMs <= 0)
        {
            return [];
        }

        var step = TickStepMs(durationMs);
        var ticks = new List<TimelineTick>();
        for (var ms = 0L; ms < durationMs; ms += step)
        {
            var label = Percent(ms, durationMs) <= LastLabelPercent ? MediaLabels.ClockTimecode(ms) : string.Empty;
            ticks.Add(new TimelineTick(ms, label));
        }

        return ticks;
    }

    /// <summary>Положение момента на ленте, % ширины (зажато в 0..100).</summary>
    public static double Percent(long ms, long durationMs) =>
        durationMs <= 0 ? 0 : Math.Clamp(ms * 100.0 / durationMs, 0, 100);

    /// <summary>Процент для CSS — инвариантной культурой (запятая сломала бы стиль).</summary>
    public static string Css(double percent) => percent.ToString("0.###", CultureInfo.InvariantCulture) + "%";

    /// <summary>Заливка отметки — цвет дорожки с прозрачностью 35 % (цвет — #rrggbb).</summary>
    public static string Fill(string color) =>
        color.Length == 7 && color[0] == '#' ? color + "59" : color;

    /// <summary>Цвет дорожки фигуранта по её порядковому номеру на ленте.</summary>
    public static string PersonColor(int index) => PersonColors[Math.Abs(index) % PersonColors.Length];

    /// <summary>
    /// Опора часов записи: встроенное время из метаданных файла, а если его нет — дата съёмки из реквизитов
    /// (приблизительно); нет ни того, ни другого — <see langword="null"/> (часы записи не показываются).
    /// </summary>
    /// <param name="recordedAt">Время начала записи по метаданным файла (ТФ-МЕД-11).</param>
    /// <param name="capturedAt">Дата съёмки, подтверждённая оператором (ТФ-МЕД-17).</param>
    /// <param name="zone">Пояс, в котором показываются времена на странице (пояс сервера).</param>
    public static RecordClock? ClockBase(DateTimeOffset? recordedAt, DateTimeOffset? capturedAt, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var (start, approximate) = recordedAt is { } exact ? (exact, false)
            : capturedAt is { } captured ? (captured, true)
            : (default(DateTimeOffset?), false);
        if (start is not { } value)
        {
            return null;
        }

        var wall = TimeZoneInfo.ConvertTime(value, zone).DateTime;
        var asUtc = new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), TimeSpan.Zero);
        return new RecordClock(asUtc.ToUnixTimeMilliseconds(), approximate);
    }

    /// <summary>
    /// Время записи в момент <paramref name="offsetMs"/>: «08.12.2022 14:08:21.345», приблизительное —
    /// «≈ 08.12.2022 14:08:21». Та же формула, что у ленты в браузере (media.js, recordClockText).
    /// </summary>
    public static string RecordTime(RecordClock clock, long offsetMs)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var moment = DateTimeOffset.FromUnixTimeMilliseconds(clock.WallClockEpochMs + Math.Max(0, offsetMs)).UtcDateTime;
        return clock.Approximate
            ? "≈ " + moment.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            : moment.ToString("dd.MM.yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>Подсказка к часам записи: откуда взято время и чего оно не доказывает.</summary>
    public static string RecordClockHint(RecordClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return clock.Approximate
            ? "Приблизительно: по дате съёмки из реквизитов носителя (указана с точностью до минуты) плюс положение в записи. "
              + "В метаданных файла времени начала записи нет."
            : "По метаданным файла: время начала записи, как его записала камера, плюс положение в записи. "
              + "Часы камеры могли быть сбиты, а метаданные — изменены: это сведения из файла, не доказательство.";
    }
}
