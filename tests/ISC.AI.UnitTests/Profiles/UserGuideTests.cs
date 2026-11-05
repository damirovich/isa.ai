using System;
using System.Linq;
using ISC.AI.Profile.Inspector;
using ISC.AI.Profile.Inspector.Application.Features.Guide;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Справка пользователя «ИнспекторAI» (страница «Помощь»): разбор текста и сверка с меню — справка не должна
/// молча отставать от разделов и вести кнопкой «Открыть раздел» в никуда.
/// </summary>
public sealed class UserGuideTests
{
    private const string HelpRoute = "/help";

    [Fact(DisplayName = "Разбор справки: вступление, раздел с секцией меню и адресом, подзаголовок, шаги, пункты, склеенный абзац")]
    public void Parses_sections_and_blocks()
    {
        const string text = """
            Вступление
            в две строки.

            ## Генератор
            Раздел меню: ИИ-инструменты
            Страница: /generator
            Готовит черновики.
            ### Как сделать справку
            1. Выберите тип.
            2. Нажмите **«Сформировать»**.
            - Совет первый
            - Совет второй

            ## Общий раздел
            Текст без адреса.
            """;

        var guide = UserGuideParser.Parse(text);

        guide.Intro.ShouldBe(["Вступление в две строки."]);
        guide.Sections.Count.ShouldBe(2);

        var generator = guide.Sections[0];
        generator.Title.ShouldBe("Генератор");
        generator.Group.ShouldBe("ИИ-инструменты");
        generator.Route.ShouldBe("/generator");
        generator.Blocks.Select(b => b.Kind).ShouldBe(
        [
            UserGuideBlockKind.Paragraph,
            UserGuideBlockKind.Subheading,
            UserGuideBlockKind.Steps,
            UserGuideBlockKind.Bullets,
        ]);
        generator.Blocks[2].Lines.ShouldBe(["Выберите тип.", "Нажмите **«Сформировать»**."]);
        generator.Blocks[3].Lines.ShouldBe(["Совет первый", "Совет второй"]);

        var common = guide.Sections[1];
        common.Group.ShouldBeNull();
        common.Route.ShouldBeNull();
        common.Blocks.ShouldHaveSingleItem().Lines.ShouldBe(["Текст без адреса."]);
    }

    [Fact(DisplayName = "«Страница:» в середине раздела — обычный текст, а не адрес раздела")]
    public void Route_line_counts_only_at_section_start()
    {
        var guide = UserGuideParser.Parse("## Раздел\nТекст.\nСтраница: /x\n");

        var section = guide.Sections.ShouldHaveSingleItem();
        section.Route.ShouldBeNull();
        section.Blocks.ShouldHaveSingleItem().Lines.ShouldBe(["Текст. Страница: /x"]);
    }

    [Fact(DisplayName = "Встроенная справка загружается: есть вступление и разделы, у каждого раздела есть содержимое")]
    public void Embedded_guide_loads()
    {
        var guide = UserGuide.Current;

        guide.Intro.ShouldNotBeEmpty();
        guide.Sections.ShouldNotBeEmpty();
        guide.Sections.ShouldAllBe(s => s.Blocks.Count > 0);
        guide.Sections.Select(s => s.Title).ShouldBeUnique();
    }

    [Fact(DisplayName = "Каждый пункт меню «ИнспекторAI» описан в справке, и каждая кнопка «Открыть раздел» ведёт на пункт меню")]
    public void Guide_covers_every_menu_item()
    {
        var menuRoutes = new InspectorProfile().Modules.Select(m => m.Route).ToHashSet(StringComparer.Ordinal);
        var guideRoutes = UserGuide.Current.Sections
            .Select(s => s.Route)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        menuRoutes.Where(r => r != HelpRoute && !guideRoutes.Contains(r))
            .ShouldBeEmpty("пункты меню без раздела в user-guide.md");
        guideRoutes.Where(r => !menuRoutes.Contains(r))
            .ShouldBeEmpty("разделы справки с адресом, которого нет в меню");
    }
}
