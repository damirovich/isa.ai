using System.Text;
using ISC.AI.Abstractions.Ingestion;

namespace ISC.AI.Ingestion;

/// <summary>
/// Простой чанкер по умолчанию (ТО-мат-03): абзацы упаковываются в фрагменты до <see cref="MaxChunkChars"/>
/// символов; слишком длинный абзац режется по длине. Базовая стратегия; может быть заменена более
/// качественной (с перекрытием/семантикой) без изменения порта.
/// </summary>
public sealed class SimpleTextChunker : ITextChunker
{
    private const int MaxChunkChars = 1000;

    private static readonly string[] ParagraphSeparators = ["\r\n\r\n", "\n\n"];

    /// <inheritdoc />
    public IReadOnlyList<string> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var rawParagraph in text.Split(ParagraphSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var paragraph = rawParagraph.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }

            if (paragraph.Length > MaxChunkChars)
            {
                Flush(chunks, current);
                for (var offset = 0; offset < paragraph.Length; offset += MaxChunkChars)
                {
                    chunks.Add(paragraph.Substring(offset, Math.Min(MaxChunkChars, paragraph.Length - offset)));
                }

                continue;
            }

            if (current.Length + paragraph.Length + 1 > MaxChunkChars)
            {
                Flush(chunks, current);
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(paragraph);
        }

        Flush(chunks, current);
        return chunks;
    }

    private static void Flush(List<string> chunks, StringBuilder buffer)
    {
        if (buffer.Length > 0)
        {
            chunks.Add(buffer.ToString());
            buffer.Clear();
        }
    }
}
