using ISC.AI.Abstractions.Security;

namespace ISC.AI.Web.Security;

/// <summary>
/// DEV-заглушка контекста доступа (регистрируется ТОЛЬКО в Development). НЕ для боевого контура:
/// на этапе Э3-08 заменяется реализацией поверх ВНЕШНЕЙ системы идентификации (SSO), маппящей
/// учётку/claims в <see cref="AccessContext"/>. Значения — из секции <c>Dev:Access</c> конфигурации,
/// по умолчанию ограничительные.
/// </summary>
public sealed class DevAccessContextProvider(IConfiguration configuration) : IAccessContextProvider
{
    /// <inheritdoc />
    public Task<AccessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var maxClassification = short.TryParse(configuration["Dev:Access:MaxClassification"], out var clearance)
            ? clearance
            : (short)0;
        var divisionId = int.TryParse(configuration["Dev:Access:DivisionId"], out var division) ? division : 0;

        return Task.FromResult(new AccessContext("dev", maxClassification, [divisionId]));
    }
}
