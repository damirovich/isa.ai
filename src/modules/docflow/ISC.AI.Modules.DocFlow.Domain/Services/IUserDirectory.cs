namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Строка справочника пользователей для выбора в UI (исполнитель, инспектор).</summary>
public sealed record UserItem(int Id, string Name);

/// <summary>
/// Порт справочника пользователей. Реестр пользователей — <c>core.app_user</c> (решение по вопросу 4
/// Э4-35: реестр переезжает в ISC.AI); реализация — в слое данных модуля поверх контекста ядра
/// (<c>*.Data</c> → <c>Persistence</c> разрешено правилом зависимостей).
/// </summary>
public interface IUserDirectory
{
    /// <summary>Список активных пользователей (идентификатор + отображаемое имя), по алфавиту.</summary>
    Task<IReadOnlyList<UserItem>> ListActiveAsync(CancellationToken cancellationToken = default);
}
