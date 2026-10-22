using System.Globalization;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Navigation;

/// <summary>
/// Адреса карточек «Следствия» для страниц пакета «Медиа» (ADR-0059): «К карточке дела» в медиатеке дела и карточке
/// носителя, имя фигуранта в легенде ленты видео. Те же маршруты, что у ссылок чата (ADR-0052).
/// </summary>
/// <remarks>
/// Адрес — только навигация: карточки дела и фигуранта по нему сами применяют решётку и роль (ТБ-020/021), поэтому
/// здесь доступ не проверяется.
/// </remarks>
public sealed class InvestigationMediaLinks : IMediaLinks
{
    /// <inheritdoc />
    public string? CaseCard(int caseId) => "/cases/" + caseId.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string? PersonCard(int personId) => "/persons/" + personId.ToString(CultureInfo.InvariantCulture);
}
