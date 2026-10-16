namespace ISC.AI.Modules.Chat.Application.Answers;

/// <summary>Вид строки ответа для показа.</summary>
public enum AnswerBlockKind
{
    /// <summary>Обычный абзац.</summary>
    Paragraph,

    /// <summary>Пункт списка («* », «- », «• »).</summary>
    Bullet,

    /// <summary>Заголовок («# …» или строка целиком жирным).</summary>
    Heading,
}

/// <summary>Кусок строки: текст, жирный ли, ссылка на фрагмент (номер) — если это «[Фрагмент N]».</summary>
/// <param name="Text">Текст как есть.</param>
/// <param name="Bold">Жирный.</param>
/// <param name="Fragment">Номер фрагмента, если кусок — ссылка на него.</param>
public sealed record AnswerSpan(string Text, bool Bold, int? Fragment);

/// <summary>Строка ответа.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="Spans">Куски.</param>
public sealed record AnswerBlock(AnswerBlockKind Kind, IReadOnlyList<AnswerSpan> Spans);

/// <summary>
/// Простое оформление ответа модели для показа (ADR-0051/0052): модель пишет списки «* », заголовки и **жирный** текст, а
/// ссылки — «[Фрагмент N]». Разбор отдаёт строки и куски, экран рисует их ТЕКСТОМ (никакой HTML из ответа в страницу не
/// попадает): ответ модели — недоверенные данные, как и материалы, из которых он собран.
/// </summary>
public static class AnswerMarkup
{
    private static readonly string[] BulletMarks = ["* ", "- ", "• ", "+ "];

    /// <summary>Строки ответа; пустые строки отброшены.</summary>
    public static IReadOnlyList<AnswerBlock> Parse(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return [];
        }

        var blocks = new List<AnswerBlock>();
        foreach (var raw in answer.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (BulletMarks.FirstOrDefault(m => line.StartsWith(m, StringComparison.Ordinal)) is { } mark)
            {
                blocks.Add(new AnswerBlock(AnswerBlockKind.Bullet, Spans(line[mark.Length..].TrimStart())));
            }
            else if (line.StartsWith('#'))
            {
                blocks.Add(new AnswerBlock(AnswerBlockKind.Heading, Spans(line.TrimStart('#').Trim(), forceBold: true)));
            }
            else if (line.Length > 4 && line.StartsWith("**", StringComparison.Ordinal) && line.EndsWith("**", StringComparison.Ordinal)
                && line.IndexOf("**", 2, StringComparison.Ordinal) == line.Length - 2)
            {
                blocks.Add(new AnswerBlock(AnswerBlockKind.Heading, Spans(line[2..^2].Trim(), forceBold: true)));
            }
            else
            {
                blocks.Add(new AnswerBlock(AnswerBlockKind.Paragraph, Spans(line)));
            }
        }

        return blocks;
    }

    // Ссылки на фрагменты — отдельными кусками; в остальном тексте пары «**» переключают жирный (жирный может охватывать
    // ссылку). Последняя непарная «**» строки остаётся текстом.
    private static List<AnswerSpan> Spans(string text, bool forceBold = false)
    {
        var parts = FragmentReferences.Split(text);
        var markers = parts.Where(p => p.Fragment is null).Sum(p => Count(p.Text));
        var toggles = markers - (markers % 2);
        var spans = new List<AnswerSpan>();
        var bold = false;
        foreach (var (part, fragment) in parts)
        {
            if (fragment is { } number)
            {
                spans.Add(new AnswerSpan(part, forceBold || bold, number));
                continue;
            }

            var pieces = part.Split("**");
            for (var i = 0; i < pieces.Length; i++)
            {
                if (i > 0)
                {
                    if (toggles > 0)
                    {
                        bold = !bold;
                        toggles--;
                    }
                    else
                    {
                        pieces[i] = "**" + pieces[i];
                    }
                }

                if (pieces[i].Length > 0)
                {
                    spans.Add(new AnswerSpan(pieces[i], forceBold || bold, null));
                }
            }
        }

        return spans;
    }

    private static int Count(string text)
    {
        var count = 0;
        for (var index = text.IndexOf("**", StringComparison.Ordinal); index >= 0; index = text.IndexOf("**", index + 2, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
