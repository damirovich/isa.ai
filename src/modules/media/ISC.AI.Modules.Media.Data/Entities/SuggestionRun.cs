using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Сверка носителя с эталонами фигурантов одного дела (<c>media.suggestion_run</c>, ТФ-ПЕР-09, ADR-0035): когда,
/// с каким числом эталонов, что запустило и сколько предложено. Нужна карточке носителя, чтобы честно отличать
/// «сверено — совпадений нет» от «ещё не сверялось». Биометрии нет — только числа; режимные поля — дела (ТБ-070),
/// чтение под решёткой (ТБ-020). Дело — слабая ссылка по значению (ТО-инф-08); носитель — FK внутри схемы с
/// каскадом: удалён носитель (ТБ-075) — уходит и его журнал сверок.
/// </summary>
public class SuggestionRun : BaseEntity, IClassified
{
    /// <summary>Носитель.</summary>
    public int AssetId { get; set; }

    /// <summary>Дело (значение из схемы профиля).</summary>
    public int CaseId { get; set; }

    /// <summary>Что запустило сверку.</summary>
    public SuggestionTrigger Trigger { get; set; }

    /// <summary>Со сколькими эталонами фигурантов дела сверено.</summary>
    public int ReferencesChecked { get; set; }

    /// <summary>Сколько новых сессий-предложений создано.</summary>
    public int SessionsCreated { get; set; }

    /// <summary>Сколько кандидатов поставлено в очередь эксперта.</summary>
    public int CandidatesCreated { get; set; }

    /// <summary>Порог сверки (косинусное расстояние).</summary>
    public double MaxCosineDistance { get; set; }

    /// <summary>Гриф дела (ТБ-070).</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение дела.</summary>
    public int DivisionId { get; set; }

    /// <summary>Навигация к носителю (каскад удаления).</summary>
    public MediaAsset? Asset { get; set; }
}
