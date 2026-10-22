using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Modules.Media.Application.Features.Navigation;

/// <summary>
/// Поставка пакета по умолчанию (ADR-0059): профиль не дал адресов своих карточек — страницы пакета переходов к ним не
/// показывают.
/// </summary>
public sealed class NoMediaLinks : IMediaLinks
{
    /// <inheritdoc />
    public string? CaseCard(int caseId) => null;

    /// <inheritdoc />
    public string? PersonCard(int personId) => null;
}
