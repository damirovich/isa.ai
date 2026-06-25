namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Извлечение основного содержимого из загруженной страницы (по типу контента). Универсальный
/// (readability-подобный) экстрактор работает на любом HTML best-effort; точные — через коннектор.
/// </summary>
public interface IContentExtractor
{
    /// <summary>Извлекает заголовок и основной текст из HTML.</summary>
    ExtractedContent Extract(string html, string sourceUrl);
}

/// <summary>Результат извлечения содержимого страницы.</summary>
/// <param name="Title">Заголовок (из &lt;title&gt;/&lt;h1&gt; best-effort).</param>
/// <param name="Text">Основной текст без навигации/скриптов.</param>
public sealed record ExtractedContent(string Title, string Text);
