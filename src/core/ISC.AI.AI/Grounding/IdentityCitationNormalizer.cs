using ISC.AI.Abstractions.Grounding;

namespace ISC.AI.AI.Grounding;

/// <summary>
/// Нормализация по умолчанию: нижний регистр + схлопывание пробелов. Нейтральна к домену; профиль
/// «ИнспекторAI» заменяет НПА-нормализатором (ru/ky-формы, номера норм/редакций — ADR-0008).
/// </summary>
public sealed class IdentityCitationNormalizer : ICitationNormalizer
{
    /// <inheritdoc />
    public string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
