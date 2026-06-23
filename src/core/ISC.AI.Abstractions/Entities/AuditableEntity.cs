namespace ISC.AI.Abstractions.Entities;

/// <summary>
/// Базовый класс аудируемой сущности (без мягкого удаления). Подходит носителям, требующим
/// гарантированного физического удаления (корпус документов любого профиля — ТБ-064): запись
/// физически удаляема, но даты создания/изменения отслеживаются.
/// </summary>
public abstract class AuditableEntity : BaseEntity, IAuditableEntity
{
}
