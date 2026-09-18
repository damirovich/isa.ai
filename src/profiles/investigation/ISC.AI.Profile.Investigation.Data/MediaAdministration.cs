using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Реализация порта пакета «Медиа» <see cref="IMediaAdministration"/>: кто вправе загружать носители,
/// искать по лицу и гарантированно удалять. Право — по РОЛИ текущего субъекта; решётка гриф/подразделение
/// (ТБ-020) и сужение по делам субъекта (ТБ-071) применяются модулем отдельно и всегда — эти проверки
/// поверх них, а не вместо.
/// </summary>
/// <remarks>
/// МАТРИЦА (ADR-0022, п. 8): загрузка — Следователь и <b>Администратор</b>; поиск по лицу — Следователь,
/// Эксперт по лицам и <b>Администратор</b>; гарантированное удаление — Администратор и Руководитель.
///
/// ОТКЛОНЕНИЕ ОТ ТП-004 по решению заказчика (2026-09-18): в ТЗ Администратор ведёт учётные записи,
/// допуски, справочники и модели, а материалами дел не работает. Заказчик распорядился открыть
/// Администратору весь функционал профиля. Причина — эксплуатационная: роль у пользователя ОДНА
/// (см. <c>investigation.user_role_assignment</c>, уникальность по пользователю), снять последнего
/// Администратора нельзя, поэтому «слепой» Администратор вынуждал заводить вторую учётную запись даже
/// для того, чтобы проверить загрузку или поиск. Тот же прецедент и по той же причине уже принят у
/// профиля «ИнспекторAI» (<c>InspectorAccessPolicy</c>, отклонение от §2.1 ТЗ СКИД от 2026-08-06).
///
/// ЧТО ЭТИМ НЕ ОСЛАБЛЕНО. (1) Решётка допуска: Администратор без записи в <c>core.clearance</c> и без
/// нужного грифа не увидит ни дела, ни носителя (ТБ-020/021, floor ядра неотключаем). (2) Сужение по
/// делам: материалы выдаются только по делам, доступным субъекту (ТБ-071). (3) Аудит: каждое обращение
/// к биометрии пишется в неизменяемый журнал с ролью-независимым составом (ТБ-072) — расширение прав
/// увеличивает объём журнала, но не уменьшает его полноту. (4) ПРАВИЛО ДВУХ ЛИЦ (ТБ-073, GATE-5)
/// сохранено полностью: <see cref="TwoPersonRule"/> живёт в модуле и профилем не переопределяется, и
/// даже с полными правами один и тот же субъект не может закрыть обе стадии верификации.
///
/// БЕЗ режима первичной настройки (в отличие от <see cref="AdministrationRule.CallerCanManageAsync"/>):
/// операции с материалами дел — не настройка контура, и «пока Администратора нет — можно всем» здесь
/// означало бы поиск по лицам без роли. Fail-closed: нет роли — нет права (ТБ-012).
/// Матрица закреплена таблицей в тесте по всем ролям и «без роли»: изменение требует правки теста и ADR.
/// </remarks>
public sealed class MediaAdministration(IUserRoleStore roles, ISubjectProvider subjectProvider) : IMediaAdministration
{
    /// <inheritdoc />
    public Task<bool> CanUploadAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerHasRoleAsync(
            roles, subjectProvider, cancellationToken,
            InvestigationRole.Investigator, InvestigationRole.Administrator);

    /// <inheritdoc />
    public Task<bool> CanSearchAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerHasRoleAsync(
            roles, subjectProvider, cancellationToken,
            InvestigationRole.Investigator, InvestigationRole.FaceExpert, InvestigationRole.Administrator);

    /// <inheritdoc />
    public Task<bool> CanPurgeAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerHasRoleAsync(
            roles, subjectProvider, cancellationToken,
            InvestigationRole.Administrator, InvestigationRole.Head);
}

/// <summary>
/// Реализация порта пакета «Медиа» <see cref="IVerificationPolicy"/>: кто вправе выступать на стадии
/// верификации. Стадия эксперта — «Эксперт по лицам», стадия верификатора — «Верификатор»; обе стадии
/// доступны и Администратору (решение заказчика 2026-09-18, см. <see cref="MediaAdministration"/>).
/// </summary>
/// <remarks>
/// ТБ-073 НЕ ослаблено: само правило двух лиц — в модуле (<see cref="TwoPersonRule"/>), профиль его не
/// переопределяет. Администратор может выступить экспертом ИЛИ верификатором по конкретному кандидату,
/// но не тем и другим сразу: второе решение от того же субъекта отклоняется и попадает в журнал как
/// отклонённая попытка. То есть «два независимых сотрудника» остаются двумя живыми людьми, и полные
/// права Администратора этого не отменяют.
/// Руководитель утверждает результат организационно (ТФ-ВЕР-02) и решений стадий не пишет.
/// </remarks>
public sealed class VerificationPolicy(IUserRoleStore roles) : IVerificationPolicy
{
    /// <inheritdoc />
    public async Task<bool> CanActAsync(VerificationStage stage, int userId, CancellationToken cancellationToken = default)
    {
        var role = await roles.GetRoleAsync(userId, cancellationToken);
        return stage switch
        {
            VerificationStage.Expert => role is InvestigationRole.FaceExpert or InvestigationRole.Administrator,
            VerificationStage.Verifier => role is InvestigationRole.Verifier or InvestigationRole.Administrator,
            _ => false,
        };
    }
}
