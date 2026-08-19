using ISC.AI.Harvester.Engine;
using Shouldly;

namespace ISC.AI.UnitTests.Harvester;

/// <summary>
/// Универсальное извлечение текста из HTML (Э4-08): заголовок + тело, без навигации/скриптов.
/// С 2026-08-19 — С СОХРАНЕНИЕМ СТРУКТУРЫ: абзацы, заголовки статей, строки таблиц не склеиваются
/// в одну строку (иначе «бюджетеКыргызской» и слепая нарезка чанкером).
/// </summary>
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

    [Fact(DisplayName = "HTML-экстрактор: абзацы и заголовки статей — отдельными строками, слова на стыках не склеиваются")]
    public void Preserves_paragraph_structure()
    {
        // Word-экспорт ЦБД: каждая статья и абзац — свой <p>; между ними раньше не было ни пробела.
        const string html = """
            <html><body>
              <p class="MsoNormal"><b>Глава I</b></p>
              <p class="MsoNormal"><b>Общие положения</b></p>
              <p class="MsoNormal"><b>Статья 1.</b></p>
              <p class="MsoNormal">В настоящем Законе используются следующие основные понятия:</p>
              <p class="MsoNormal"><b>Чрезвычайное положение</b> - временная мера, вводимая на всей территории Кыргызской Республики.</p>
              <p class="MsoNormal"><b>Статья 2.</b></p>
              <p class="MsoNormal">(Утратила силу в соответствии с Законом КР от 29 декабря 2011 года № 256)</p>
            </body></html>
            """;

        var text = new HtmlContentExtractor().Extract(html, "http://cbd/1").Text;
        var lines = text.Split('\n');

        // Заголовки статей — в начале собственных строк (так их ищет чанкер НПА).
        lines.ShouldContain("Статья 1.");
        lines.ShouldContain("Статья 2.");
        lines.ShouldContain("Глава I");
        // Стык абзацев не склеивает слова.
        text.ShouldNotContain("понятия:Чрезвычайное");
        text.ShouldNotContain("РеспубликиСтатья");
        // Между абзацами — ровно одна пустая строка (граница абзаца для чанкеров), не больше.
        text.ShouldNotContain("\n\n\n");
        text.ShouldContain("Статья 1.\n\nВ настоящем Законе");
    }

    [Fact(DisplayName = "HTML-экстрактор: таблица — построчно с разделителем ячеек, а не каша чисел")]
    public void Renders_table_rows()
    {
        const string html = """
            <html><body>
              <p>Статья 2. Доходы республиканского бюджета:</p>
              <table>
                <tr><td>Налог на добавленную стоимость</td><td>24725025</td></tr>
                <tr><td>Налог на имущество</td><td>3366952</td></tr>
              </table>
              <p>Статья 3. Расходы.</p>
            </body></html>
            """;

        var text = new HtmlContentExtractor().Extract(html, "http://cbd/2").Text;
        var lines = text.Split('\n');

        lines.ShouldContain("Налог на добавленную стоимость | 24725025");
        lines.ShouldContain("Налог на имущество | 3366952");
        // Строки таблицы не слипаются друг с другом и со статьёй после таблицы.
        text.ShouldNotContain("24725025Налог");
        text.ShouldNotContain("3366952Статья");
    }

    [Fact(DisplayName = "HTML-экстрактор: <br> и элементы списка — переносы строк, неразрывные пробелы — обычные")]
    public void Handles_line_breaks_and_nbsp()
    {
        const string html = """
            <html><body>
              <p>Чрезвычайное&nbsp;положение вводится:<br>1) биологического характера;<br>2) социального характера.</p>
              <ul><li>первый пункт</li><li>второй пункт</li></ul>
            </body></html>
            """;

        var text = new HtmlContentExtractor().Extract(html, "http://cbd/3").Text;
        var lines = text.Split('\n');

        lines.ShouldContain("Чрезвычайное положение вводится:");
        lines.ShouldContain("1) биологического характера;");
        lines.ShouldContain("2) социального характера.");
        lines.ShouldContain("первый пункт");
        lines.ShouldContain("второй пункт");
        text.ShouldNotContain(' ');
    }
}
