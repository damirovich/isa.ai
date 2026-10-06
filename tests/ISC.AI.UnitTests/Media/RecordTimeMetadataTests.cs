using System;
using System.Collections.Generic;
using ISC.AI.Modules.Media.Domain.Model;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Встроенное время начала записи из метаданных видео (ТФ-МЕД-11, ADR-0038): какой тег берётся первым, как читаются
/// UTC, смещение пояса и время без пояса, что отбрасывается (нулевые эпохи, будущее, дата без времени).
/// </summary>
public sealed class RecordTimeMetadataTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);

    // Пояс без перехода на летнее время: результат теста не зависит от машины.
    private static readonly TimeZoneInfo Bishkek = TimeZoneInfo.CreateCustomTimeZone("test+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");

    private static Dictionary<string, string> Tags(params (string Key, string Value)[] pairs)
    {
        var tags = new Dictionary<string, string>();
        foreach (var (key, value) in pairs)
        {
            tags[key] = value;
        }

        return tags;
    }

    [Fact(DisplayName = "creation_time контейнера в UTC («…Z», шесть знаков долей) — как записан")]
    public void Container_creation_time_utc()
    {
        var value = RecordTimeMetadata.TryParse(Tags(("creation_time", "2022-12-08T08:08:12.000000Z")), null, Bishkek, Now);

        value.ShouldBe(new DateTimeOffset(2022, 12, 8, 8, 8, 12, TimeSpan.Zero));
    }

    [Fact(DisplayName = "Тег Apple со смещением «+0600» важнее creation_time: местное время камеры с поясом")]
    public void Apple_creation_date_wins()
    {
        var value = RecordTimeMetadata.TryParse(
            Tags(("creation_time", "2022-12-08T08:08:00.000000Z"), ("com.apple.quicktime.creationdate", "2022-12-08T14:08:12+0600")),
            null, Bishkek, Now);

        value.ShouldBe(new DateTimeOffset(2022, 12, 8, 14, 8, 12, TimeSpan.FromHours(6)));
    }

    [Fact(DisplayName = "Время без пояса (AVI IDIT) — время пояса сервера; регистр ключа не важен")]
    public void Zoneless_value_is_local_time()
    {
        var value = RecordTimeMetadata.TryParse(Tags(("CREATION_TIME", "2003-03-10 15:04:43")), null, Bishkek, Now);

        value.ShouldBe(new DateTimeOffset(2003, 3, 10, 15, 4, 43, TimeSpan.FromHours(6)));
    }

    [Fact(DisplayName = "Нет у контейнера — берётся creation_time видеопотока")]
    public void Falls_back_to_stream_tag()
    {
        var value = RecordTimeMetadata.TryParse(
            Tags(("encoder", "Lavf")), Tags(("creation_time", "2024-05-01T10:00:00Z")), Bishkek, Now);

        value.ShouldBe(new DateTimeOffset(2024, 5, 1, 10, 0, 0, TimeSpan.Zero));
    }

    [Theory(DisplayName = "Отбрасываются: нулевые эпохи 1904/1970, будущее, только дата, мусор; следующий тег всё равно читается")]
    [InlineData("1904-01-01T00:00:00.000000Z")]
    [InlineData("1970-01-01T00:00:00.000000Z")]
    [InlineData("2031-01-01T00:00:00Z")]
    [InlineData("2003-03-10")]
    [InlineData("вчера вечером")]
    public void Implausible_values_are_ignored(string raw)
    {
        RecordTimeMetadata.TryParse(Tags(("creation_time", raw)), null, Bishkek, Now).ShouldBeNull();

        // Неправдоподобный тег контейнера не мешает правдоподобному тегу потока.
        RecordTimeMetadata.TryParse(Tags(("creation_time", raw)), Tags(("creation_time", "2022-12-08T08:08:12Z")), Bishkek, Now)
            .ShouldBe(new DateTimeOffset(2022, 12, 8, 8, 8, 12, TimeSpan.Zero));
    }

    [Fact(DisplayName = "Тегов нет вовсе — null")]
    public void No_tags_no_time()
    {
        RecordTimeMetadata.TryParse(null, null, Bishkek, Now).ShouldBeNull();
        RecordTimeMetadata.TryParse(Tags(), Tags(), Bishkek, Now).ShouldBeNull();
    }
}
