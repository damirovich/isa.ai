using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ISC.AI.Profile.Inspector.Application.Features.Guide;

/// <summary>Вид блока справки.</summary>
public enum UserGuideBlockKind
{
    /// <summary>Абзац текста.</summary>
    Paragraph,

    /// <summary>Подзаголовок внутри раздела.</summary>
    Subheading,

    /// <summary>Нумерованные шаги «что нажать».</summary>
    Steps,

    /// <summary>Маркированный список.</summary>
    Bullets,
}

/// <summary>Блок раздела справки: у абзаца и подзаголовка одна строка, у списков — по строке на пункт.</summary>
/// <param name="Kind">Вид блока.</param>
/// <param name="Lines">Строки блока (могут содержать выделение <c>**жирным**</c>).</param>
public sealed record UserGuideBlock(UserGuideBlockKind Kind, IReadOnlyList<string> Lines);

/// <summary>Раздел справки — как правило, один пункт меню.</summary>
/// <param name="Title">Название раздела.</param>
/// <param name="Group">Секция меню, к которой относится раздел; <see langword="null"/> — без секции.</param>
/// <param name="Route">Адрес страницы раздела; <see langword="null"/> — общий раздел без страницы.</param>
/// <param name="Blocks">Содержимое раздела по порядку.</param>
public sealed record UserGuideSection(string Title, string? Group, string? Route, IReadOnlyList<UserGuideBlock> Blocks)
{
    /// <summary>Весь текст раздела одной строкой — для поиска по справке.</summary>
    public string PlainText => Title + " " + string.Join(" ", Blocks.SelectMany(b => b.Lines));

    /// <summary>
    /// Раздел текстом для модели (ИИ-помощник, ADR-0067): название, секция меню, адрес и содержимое с сохранённой
    /// нумерацией шагов — чтобы ответ мог пересказать «что нажать» по порядку.
    /// </summary>
    public string ToModelText()
    {
        var text = new StringBuilder();
        text.Append("Раздел справки: ").Append(Title).Append('\n');
        if (Group is { } group)
        {
            text.Append("Секция меню: ").Append(group).Append('\n');
        }

        if (Route is { } route)
        {
            text.Append("Адрес страницы: ").Append(route).Append('\n');
        }

        foreach (var block in Blocks)
        {
            switch (block.Kind)
            {
                case UserGuideBlockKind.Subheading:
                    text.Append(block.Lines[0]).Append(":\n");
                    break;
                case UserGuideBlockKind.Steps:
                    for (var i = 0; i < block.Lines.Count; i++)
                    {
                        text.Append(i + 1).Append(". ").Append(block.Lines[i]).Append('\n');
                    }

                    break;
                case UserGuideBlockKind.Bullets:
                    foreach (var line in block.Lines)
                    {
                        text.Append("- ").Append(line).Append('\n');
                    }

                    break;
                default:
                    text.Append(block.Lines[0]).Append('\n');
                    break;
            }
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>
/// Справка пользователя «ИнспекторAI»: что делает каждый раздел и что нажимать. Текст — один файл
/// <c>user-guide.md</c> (встроенный ресурс), его правят без программиста; страница «Помощь» показывает разобранный текст.
/// </summary>
/// <param name="Intro">Вступительные абзацы до первого раздела.</param>
/// <param name="Sections">Разделы в порядке файла.</param>
public sealed record UserGuide(IReadOnlyList<string> Intro, IReadOnlyList<UserGuideSection> Sections)
{
    /// <summary>Имя встроенного ресурса с текстом справки (задано в .csproj через LogicalName).</summary>
    public const string ResourceName = "ISC.AI.Profile.Inspector.UserGuide.md";

    private static readonly Lazy<UserGuide> Embedded = new(LoadEmbedded);

    /// <summary>Справка из встроенного ресурса сборки (разбирается один раз на процесс).</summary>
    public static UserGuide Current => Embedded.Value;

    private static UserGuide LoadEmbedded()
    {
        using var stream = typeof(UserGuide).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"В сборке нет встроенного ресурса справки «{ResourceName}».");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return UserGuideParser.Parse(reader.ReadToEnd());
    }
}

/// <summary>
/// Разбор текста справки — узкое подмножество Markdown, достаточное для инструкции:
/// <c>## Раздел</c>, служебные строки <c>Раздел меню: …</c> и <c>Страница: /адрес</c> сразу под заголовком,
/// <c>### Подзаголовок</c>, шаги <c>1. …</c>, пункты <c>- …</c>, остальное — абзацы (соседние строки склеиваются).
/// </summary>
/// <remarks>
/// Свой разбор, а не библиотека Markdown: распространённые парсеры для .NET — не под MIT/Apache, а нужен только
/// этот набор конструкций.
/// </remarks>
public static partial class UserGuideParser
{
    private const string GroupPrefix = "Раздел меню:";
    private const string RoutePrefix = "Страница:";

    /// <summary>Разбирает текст справки.</summary>
    public static UserGuide Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var intro = new List<string>();
        var sections = new List<UserGuideSection>();

        string? title = null;
        string? group = null;
        string? route = null;
        var blocks = new List<UserGuideBlock>();
        var paragraph = new List<string>();
        var list = new List<string>();
        var listKind = UserGuideBlockKind.Bullets;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            var joined = string.Join(" ", paragraph);
            if (title is null)
            {
                intro.Add(joined);
            }
            else
            {
                blocks.Add(new UserGuideBlock(UserGuideBlockKind.Paragraph, [joined]));
            }

            paragraph.Clear();
        }

        void FlushList()
        {
            if (list.Count > 0)
            {
                blocks.Add(new UserGuideBlock(listKind, [.. list]));
                list.Clear();
            }
        }

        void FlushSection()
        {
            FlushParagraph();
            FlushList();
            if (title is not null)
            {
                sections.Add(new UserGuideSection(title, group, route, [.. blocks]));
            }

            blocks.Clear();
            group = null;
            route = null;
        }

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
            {
                FlushParagraph();
                FlushList();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushSection();
                title = line[3..].Trim();
                continue;
            }

            // Служебные строки — только в самом начале раздела, до первого блока.
            if (title is not null && blocks.Count == 0 && paragraph.Count == 0 && list.Count == 0)
            {
                if (line.StartsWith(GroupPrefix, StringComparison.Ordinal))
                {
                    group = line[GroupPrefix.Length..].Trim();
                    continue;
                }

                if (line.StartsWith(RoutePrefix, StringComparison.Ordinal))
                {
                    route = line[RoutePrefix.Length..].Trim();
                    continue;
                }
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                FlushParagraph();
                FlushList();
                blocks.Add(new UserGuideBlock(UserGuideBlockKind.Subheading, [line[4..].Trim()]));
                continue;
            }

            if (StepPattern().Match(line) is { Success: true } step)
            {
                FlushParagraph();
                if (listKind != UserGuideBlockKind.Steps)
                {
                    FlushList();
                }

                listKind = UserGuideBlockKind.Steps;
                list.Add(step.Groups["text"].Value);
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (listKind != UserGuideBlockKind.Bullets)
                {
                    FlushList();
                }

                listKind = UserGuideBlockKind.Bullets;
                list.Add(line[2..].Trim());
                continue;
            }

            FlushList();
            paragraph.Add(line);
        }

        FlushSection();
        return new UserGuide(intro, sections);
    }

    [GeneratedRegex(@"^\d+\.\s+(?<text>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex StepPattern();
}
