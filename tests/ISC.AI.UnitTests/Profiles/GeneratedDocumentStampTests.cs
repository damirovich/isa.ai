using System;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Штамп ИИ-происхождения при регистрации сформированного документа (ТБ-042): источник и примечание
/// обязаны честно называть ИИ и фиксировать итог грунтовки — по ним документ отличим от написанного
/// человеком и через год.
/// </summary>
public sealed class GeneratedDocumentStampTests
{
    [Fact(DisplayName = "Источник и примечание называют ИИ, модуль и итог грунтовки")]
    public void Stamp_names_ai_origin_and_grounding_summary()
    {
        GeneratedDocumentStamp.Source("Генератор").ShouldBe("ИнспекторAI · сформировано ИИ (Генератор)");

        var notes = GeneratedDocumentStamp.Notes("Редактор", new DateOnly(2026, 8, 24),
        [
            new CitationCheck("Приказ N1", CitationStatus.Confirmed, 3),
            new CitationCheck("Приказ N2", CitationStatus.Confirmed, 5),
        ]);

        notes.ShouldContain("сформирован ИИ (Редактор) 24.08.2026");
        notes.ShouldContain("принят к регистрации человеком");
        notes.ShouldContain("правовых ссылок 2, подтверждено 2");
    }
}
