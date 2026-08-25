using System.Globalization;
using System.Text;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Meetings;

/// <summary>
/// ДЕТЕРМИНИРОВАННЫЙ факт-блок для справки об исполнении (ТФ-СОВ-02): состояние пунктов протокола
/// считает КОД из данных документооборота — ИИ пишет только связующий текст (запрет других фактов —
/// в промпт-шаблоне).
/// </summary>
public static class MeetingReportFacts
{
    /// <summary>Собирает факт-блок по протоколу.</summary>
    public static string Build(MeetingProtocol protocol)
    {
        var done = protocol.Items.Count(i => i.IsDone);
        var overdue = protocol.Items.Count(i => i.IsOverdue);
        var inProgress = protocol.Items.Count - done - overdue;

        var facts = new StringBuilder();
        facts.AppendLine(CultureInfo.InvariantCulture,
            $"Протокол: {(protocol.RegNumber is { } number ? $"№ {number}" : "без номера")} "
            + $"от {protocol.RegDate:dd.MM.yyyy}. Содержание: {protocol.ShortContent}");
        facts.AppendLine(CultureInfo.InvariantCulture,
            $"Пунктов (поручений): {protocol.Items.Count}; исполнено: {done}; "
            + $"в работе: {inProgress}; ПРОСРОЧЕНО: {overdue}.");

        if (protocol.Items.Count == 0)
        {
            facts.AppendLine("Поручений по протоколу не назначено.");
        }

        var ordinal = 0;
        foreach (var item in protocol.Items)
        {
            ordinal++;
            facts.AppendLine(CultureInfo.InvariantCulture,
                $"— Пункт {ordinal}: {item.DivisionName}; срок {(item.Deadline is { } deadline ? deadline.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "не установлен")}; "
                + $"состояние: {item.StatusLabel}{(item.IsOverdue ? " (ТРЕБУЕТ ЭСКАЛАЦИИ)" : string.Empty)}.");
        }

        return facts.ToString();
    }
}
