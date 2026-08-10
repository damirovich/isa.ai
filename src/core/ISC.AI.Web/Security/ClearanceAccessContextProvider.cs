using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence.Security;

namespace ISC.AI.Web.Security;

/// <summary>
/// Боевой провайдер контекста доступа (Э3-08, ТБ-011/012): субъект — из аутентифицированной сессии,
/// допуск — из <c>core.clearance</c> ЧТЕНИЕМ НА КАЖДУЮ ОПЕРАЦИЮ (не из клеймов cookie).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ БЕЗОПАСНОСТИ (ТД-004): допуск не кэшируется в сессии — отзыв действует для retrieval
/// немедленно (ТБ-016). FAIL-CLOSED (ТБ-012/021): нет аутентификации / нет пользователя / нет
/// действующего допуска — <see cref="AccessContextRequiredException"/>, а не «пустой» доступ.
/// Разбор принципала вынесен в <see cref="ISubjectProvider"/> — тот же код обслуживает
/// администрирование допусков и ролей, которое обязано работать ДО появления первой записи допуска
/// (см. remarks у порта).
/// </remarks>
public sealed class ClearanceAccessContextProvider(
    ClearanceAccessReader clearanceReader,
    ISubjectProvider subjectProvider) : IAccessContextProvider
{
    /// <inheritdoc />
    public async Task<AccessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
        {
            throw new AccessContextRequiredException();
        }

        // Свежее чтение допуска из БД — отзыв/деактивация видны немедленно (ТБ-016).
        return await clearanceReader.ReadAsync(userId, cancellationToken)
            ?? throw new AccessContextRequiredException();
    }
}
