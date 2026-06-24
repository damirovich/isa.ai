namespace ISC.AI.Abstractions.Security;

/// <summary>
/// Контекст доступа субъекта — ОБЯЗАТЕЛЬНЫЙ вход фильтра извлечения (ТБ-012, ТБ-020). Несёт допуск
/// (максимальный доступный гриф) и разрешённые подразделения. Отсутствие контекста ⇒ доступ запрещён
/// (fail-closed, ТБ-021). Роль/доменные уточнения подключаются профилем через <see cref="IAccessPolicy"/>.
/// </summary>
public sealed record AccessContext(
    string SubjectId,
    short MaxClassification,
    IReadOnlyCollection<int> AllowedDivisions);
