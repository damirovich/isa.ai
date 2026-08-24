using System.Globalization;
using ISC.AI.Abstractions.Grounding;

namespace ISC.AI.Profile.Inspector.Application.Features.Generation;

/// <summary>
/// Штамп ИИ-происхождения для регистрации сформированного документа в документообороте:
/// источник и примечание. ЧЕСТНОСТЬ (ТБ-042): документ, написанный ИИ, обязан быть помечен —
/// в карточке (поле «Источник»), в примечании (итог грунтовки) и в самом файле (штамп экспортёра).
/// </summary>
public static class GeneratedDocumentStamp
{
    /// <summary>Значение поля «Источник» документа: видно в списке и карточке документооборота.</summary>
    public static string Source(string origin) => $"ИнспекторAI · сформировано ИИ ({origin})";

    /// <summary>
    /// Примечание к документу: кем/когда сформирован, что принят человеком, итог грунтовки.
    /// Регистрация допускается только при полностью подтверждённой грунтовке (GATE-2) —
    /// счётчики здесь фиксируют это состояние в карточке навсегда.
    /// </summary>
    public static string Notes(string origin, DateOnly date, IReadOnlyList<CitationCheck> citations)
    {
        var confirmed = citations.Count(c => c.Status == CitationStatus.Confirmed);
        return string.Create(CultureInfo.InvariantCulture,
            $"Черновик сформирован ИИ ({origin}) {date:dd.MM.yyyy}; проверен и принят к регистрации человеком (HITL, ТБ-042). "
            + $"Грунтовка: правовых ссылок {citations.Count}, подтверждено {confirmed}.");
    }
}
