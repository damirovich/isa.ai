using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Реализация порта <see cref="IPlatformAdministration"/> — кто в профиле «ИнспекторAI» вправе вести
/// учётные записи и допуски и кто вправе читать неизменяемый журнал аудита (ТБ-012/030/032).
/// </summary>
/// <remarks>
/// Инверсия зависимости, как у документооборота (ADR-0017, ADR-0023): право определяется РОЛЬЮ,
/// роли ведёт профиль, а пакет администрирования на профиль не ссылается. Без этой регистрации
/// право не имеет никто — осознанный fail-closed (ТС-013).
/// </remarks>
public sealed class InspectorPlatformAdministration(IUserRoleStore roles, ISubjectProvider subjectProvider)
    : IPlatformAdministration
{
    /// <inheritdoc />
    /// <remarks>
    /// Правило ОДНО со справочниками профиля и настройками документооборота
    /// (<see cref="AdministrationRule"/>): Администратор — всегда; любой вошедший — только пока
    /// Администратора в системе нет. Вторая половина не послабление, а выход из «замка без ключа»
    /// (6.4.1): после чистого развёртывания роль назначить некому, потому что назначение роли само
    /// требует роли.
    /// </remarks>
    public Task<bool> CanManageAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// У «ИнспекторAI» журнал читает ТОТ ЖЕ Администратор: отдельной роли офицера ИБ в профиле нет
    /// (§2.1 ТЗ СКИД — четыре роли), и выдумывать её здесь нельзя. Порт разделяет два вопроса ради
    /// профиля «Следствие», где читатель журнала — отдельная роль (ТП-004); для инспекции ответ
    /// совпадает, и это записано явно, а не оставлено на догадку читателя.
    /// </remarks>
    public Task<bool> CanViewAuditAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken);
}
