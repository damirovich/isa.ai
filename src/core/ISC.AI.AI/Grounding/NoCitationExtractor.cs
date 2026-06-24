using ISC.AI.Abstractions.Grounding;

namespace ISC.AI.AI.Grounding;

/// <summary>
/// Экстрактор ссылок по умолчанию: ничего не извлекает. Доменное извлечение (для НПА — номера
/// норм/статей) поставляет профиль, регистрируя свою <see cref="ICitationExtractor"/>.
/// </summary>
public sealed class NoCitationExtractor : ICitationExtractor
{
    /// <inheritdoc />
    public IReadOnlyList<string> Extract(string output) => [];
}
