using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Справочник пользователей для тестов, где предмет проверки — НЕ правило «исполнитель видит своё
/// подразделение». По умолчанию разрешает всё: иначе каждый тест документов пришлось бы снабжать
/// пользователями и допусками, и настоящая проверка утонула бы в подготовке данных.
/// </summary>
/// <remarks>
/// Само правило (<see cref="IUserDirectory.CanSeeDivisionAsync"/>) проверяется отдельно, на РЕАЛЬНОМ
/// справочнике поверх <c>core.app_user</c>/<c>core.clearance</c> — см. тесты назначений; подменять
/// его «разрешено всё» там было бы самообманом.
/// </remarks>
internal sealed class TestUserDirectory(bool allowDivisions = true) : IUserDirectory
{
    /// <summary>Разрешающий справочник — значение по умолчанию для тестов не про доступ.</summary>
    public static TestUserDirectory AllowAll { get; } = new();

    /// <summary>Запрещающий справочник — для проверки отказа «исполнителю не разрешено подразделение».</summary>
    public static TestUserDirectory DenyAll { get; } = new(allowDivisions: false);

    public Task<IReadOnlyList<UserItem>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserItem>>([]);

    public Task<string?> GetNameAsync(int userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>($"Пользователь {userId}");

    public Task<bool> CanSeeDivisionAsync(
        int userId, int divisionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(allowDivisions);
}
