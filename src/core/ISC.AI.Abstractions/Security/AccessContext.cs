using System.Globalization;

namespace ISC.AI.Abstractions.Security;

/// <summary>
/// Контекст доступа субъекта — ОБЯЗАТЕЛЬНЫЙ вход фильтра извлечения (ТБ-012, ТБ-020). Несёт допуск
/// (максимальный доступный гриф) и разрешённые подразделения. Отсутствие контекста ⇒ доступ запрещён
/// (fail-closed, ТБ-021). Роль/доменные уточнения подключаются профилем через <see cref="IAccessPolicy"/>.
/// </summary>
public sealed record AccessContext(
    string SubjectId,
    short MaxClassification,
    IReadOnlyCollection<int> AllowedDivisions)
{
    /// <summary>
    /// Числовой идентификатор субъекта для колонки «кто» неизменяемого журнала (ТБ-030): локальный id
    /// пользователя (Э3-08). Нечисловой <see cref="SubjectId"/> (dev-заглушка и т.п.) в аудит не пишется —
    /// здесь <see langword="null"/>. Единая точка правила: используется и сквозным <c>AuditBehavior</c>,
    /// и прямой записью аудита генерации (<c>GroundedGenerator</c>), чтобы разбор не расходился.
    /// </summary>
    public int? NumericSubjectId =>
        int.TryParse(SubjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
}
