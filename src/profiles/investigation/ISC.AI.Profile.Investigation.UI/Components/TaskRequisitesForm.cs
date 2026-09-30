using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Изменяемая модель полей реквизитов задания (ТФ-ДЕЛ-05) для форм заведения и правки дела. Одна модель на
/// обе формы — чтобы набор полей и правило «что обязательно» не расходились между страницами. Окончательную
/// проверку делают валидатор команды и хранилище (сверка со справочниками, ТФ-АДМ-07).
/// </summary>
public sealed class TaskRequisitesForm
{
    /// <summary>№ задания.</summary>
    public string? TaskNumber { get; set; }

    /// <summary>Подразделение-инициатор (ГУ).</summary>
    public int? InitiatorUnitId { get; set; }

    /// <summary>ФИО инициатора.</summary>
    public string? InitiatorName { get; set; }

    /// <summary>Звание инициатора.</summary>
    public int? InitiatorRankId { get; set; }

    /// <summary>Должность инициатора.</summary>
    public int? InitiatorPositionId { get; set; }

    /// <summary>Контактный телефон инициатора.</summary>
    public string? InitiatorPhone { get; set; }

    /// <summary>Прочие служебные реквизиты инициатора.</summary>
    public string? InitiatorDetails { get; set; }

    /// <summary>Обоснование мероприятия.</summary>
    public string? Justification { get; set; }

    /// <summary>Цель мероприятия.</summary>
    public string? Purpose { get; set; }

    /// <summary>Примечание.</summary>
    public string? Notes { get; set; }

    /// <summary>ГУ, стоявший в деле при открытии формы (остаётся в списке выбора, даже если выключен).</summary>
    public int? InitialUnitId { get; private init; }

    /// <summary>Звание, стоявшее в деле при открытии формы.</summary>
    public int? InitialRankId { get; private init; }

    /// <summary>Должность, стоявшая в деле при открытии формы.</summary>
    public int? InitialPositionId { get; private init; }

    /// <summary>Заполнены ли обязательные реквизиты (№, ГУ, обоснование, цель) — для доступности кнопки сохранения.</summary>
    public bool IsComplete => MissingHint is null;

    /// <summary>Каких обязательных реквизитов задания не хватает — подсказка у неактивной кнопки; всё есть — <see langword="null"/>.</summary>
    public string? MissingHint => FormHints.Missing(
        (string.IsNullOrWhiteSpace(TaskNumber), "№ задания"),
        (InitiatorUnitId is not > 0, "подразделение-инициатор"),
        (string.IsNullOrWhiteSpace(Justification), "обоснование"),
        (string.IsNullOrWhiteSpace(Purpose), "цель"));

    /// <summary>Модель по реквизитам дела (пустая — для нового задания).</summary>
    public static TaskRequisitesForm From(TaskRequisites? task) => task is null
        ? new TaskRequisitesForm()
        : new TaskRequisitesForm
        {
            TaskNumber = task.TaskNumber,
            InitiatorUnitId = task.InitiatorUnitId,
            InitiatorName = task.InitiatorName,
            InitiatorRankId = task.InitiatorRankId,
            InitiatorPositionId = task.InitiatorPositionId,
            InitiatorPhone = task.InitiatorPhone,
            InitiatorDetails = task.InitiatorDetails,
            Justification = task.Justification,
            Purpose = task.Purpose,
            Notes = task.Notes,
            InitialUnitId = task.InitiatorUnitId,
            InitialRankId = task.InitiatorRankId,
            InitialPositionId = task.InitiatorPositionId,
        };

    /// <summary>Реквизиты для команды; вызывать только при <see cref="IsComplete"/>.</summary>
    public TaskRequisites ToRequisites() => new(
        TaskNumber!.Trim(),
        InitiatorUnitId!.Value,
        Justification!.Trim(),
        Purpose!.Trim(),
        Clean(InitiatorName),
        InitiatorRankId,
        InitiatorPositionId,
        Clean(InitiatorPhone),
        Clean(InitiatorDetails),
        Clean(Notes));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Выборки из справочников профиля для форм и карточек (ТФ-АДМ-07).</summary>
public static class ReferenceLookup
{
    /// <summary>
    /// Варианты выбора: действующие записи вида плюс уже выбранная и стоявшая в деле при открытии формы
    /// (<paramref name="initial"/>), даже если их выключили. Иначе поле старого задания показывало бы пустоту
    /// вместо своего ГУ, а после выбора другого ГУ к исходному было бы не вернуться. Сервер примет выключенную
    /// запись, только если она уже стоит в деле (CaseStore).
    /// </summary>
    public static IEnumerable<ReferenceItemRow> Options(
        IEnumerable<ReferenceItemRow> items, ReferenceKind kind, int? selected, int? initial = null) =>
        items.Where(i => i.Kind == kind && (i.IsActive || i.Id == selected || i.Id == initial))
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Name, StringComparer.CurrentCulture);

    /// <summary>Наименование записи; для неизвестной — «№ id», для пустой ссылки — «—».</summary>
    public static string Name(IEnumerable<ReferenceItemRow> items, int? id) => id is not { } value
        ? "—"
        : items.FirstOrDefault(i => i.Id == value)?.Name ?? $"№ {value}";
}
