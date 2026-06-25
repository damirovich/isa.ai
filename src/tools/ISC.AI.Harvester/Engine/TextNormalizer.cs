using System.Text;

namespace ISC.AI.Harvester.Engine;

/// <summary>Свёртка пробельных последовательностей в один пробел и обрезка краёв (общий помощник извлечения).</summary>
internal static class TextNormalizer
{
    /// <summary>Сжимает любые серии пробельных символов до одного пробела; обрезает края.</summary>
    public static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWhitespace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWhitespace)
                {
                    builder.Append(' ');
                }

                lastWhitespace = true;
            }
            else
            {
                builder.Append(ch);
                lastWhitespace = false;
            }
        }

        return builder.ToString().Trim();
    }
}
