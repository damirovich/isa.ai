using System;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Profile.Investigation.UI;
using MudBlazor;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Подписи носителей в карточке дела профиля (ADR-0026): каждое значение перечислений пакета «Медиа»
/// подписано по-русски — появление нового значения без подписи ловится здесь, а не глазами заказчика.
/// </summary>
public sealed class CaseMediaLabelsTests
{
    [Fact(DisplayName = "Виды носителя: Фото, Видео, Аудио")]
    public void Kind_labels_are_russian()
    {
        CaseMediaLabels.Kind(MediaKind.Image).ShouldBe("Фото");
        CaseMediaLabels.Kind(MediaKind.Video).ShouldBe("Видео");
        CaseMediaLabels.Kind(MediaKind.Audio).ShouldBe("Аудио");
    }

    [Fact(DisplayName = "Ни один вид носителя, статус индексации или расшифровки не выводится сырым именем перечисления")]
    public void No_enum_value_leaks_as_raw_name()
    {
        foreach (var kind in Enum.GetValues<MediaKind>())
        {
            CaseMediaLabels.Kind(kind).ShouldNotBe(kind.ToString());
        }

        foreach (var status in Enum.GetValues<MediaIndexStatus>())
        {
            CaseMediaLabels.Index(status).ShouldNotBe(status.ToString());
        }

        foreach (var status in Enum.GetValues<TranscriptStatus>())
        {
            CaseMediaLabels.Transcript(status).ShouldNotBe(status.ToString());
        }
    }

    [Fact(DisplayName = "«Поиск по лицу неприменим» — нейтральным цветом: это не сбой и не успех")]
    public void Not_applicable_index_is_neutral()
    {
        CaseMediaLabels.Index(MediaIndexStatus.NotApplicable).ShouldBe("Поиск по лицу неприменим");
        CaseMediaLabels.IndexColor(MediaIndexStatus.NotApplicable).ShouldBe(Color.Default);
    }
}
