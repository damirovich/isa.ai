using System.Threading;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Подмена настроек поиска и верификации (ADR-0054) для модульных тестов. По умолчанию — режим ДВУХ ЛИЦ: так написаны
/// сценарии верификации до появления режима; сценарии одного оператора передают режим явно.
/// </summary>
internal static class MediaTestSettings
{
    /// <summary>Хранилище настроек, отдающее заданные значения.</summary>
    public static IMediaSettingsStore Store(
        ConfirmationMode mode = ConfirmationMode.TwoPerson,
        int thresholdPercent = MediaSettings.DefaultThresholdPercent,
        int minThresholdPercent = MediaSettings.DefaultMinThresholdPercent)
    {
        var store = Substitute.For<IMediaSettingsStore>();
        store.GetAsync(Arg.Any<CancellationToken>()).Returns(new MediaSettings(mode, thresholdPercent, minThresholdPercent));
        return store;
    }
}
