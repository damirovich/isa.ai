using ISC.AI.Harvester.Engine;
using Shouldly;

namespace ISC.AI.UnitTests.Harvester;

/// <summary>Универсальное извлечение текста из HTML (Э4-08): заголовок + тело, без навигации/скриптов.</summary>
public sealed class HtmlContentExtractorTests
{
    [Fact(DisplayName = "HTML-экстрактор: берёт заголовок и текст тела, отбрасывает скрипты/навигацию/футер")]
    public void Extracts_title_and_body_drops_noise()
    {
        const string html = """
            <html><head><title>Заголовок страницы</title></head>
            <body>
              <nav>Меню Главная Контакты</nav>
              <script>var x = 1;</script>
              <h1>Заголовок</h1>
              <p>Основной текст документа.</p>
              <footer>Подвал 2026</footer>
            </body></html>
            """;

        var result = new HtmlContentExtractor().Extract(html, "http://example/x");

        result.Title.ShouldBe("Заголовок страницы");
        result.Text.ShouldContain("Основной текст документа.");
        result.Text.ShouldNotContain("var x");
        result.Text.ShouldNotContain("Меню");
        result.Text.ShouldNotContain("Подвал");
    }
}
