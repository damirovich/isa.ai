using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Реализация порта пакета «Медиа» <see cref="IMediaAdministration"/>: кто вправе загружать носители,
/// искать по лицу и гарантированно удалять (ТП-004). Право — по РОЛИ текущего субъекта; решётка
/// гриф/подразделение (ТБ-020) применяется модулем отдельно и всегда — эти проверки поверх неё.
/// </summary>
/// <remarks>
/// БЕЗ режима первичной настройки (в отличие от <see cref="AdministrationRule.CallerCanManageAsync"/>):
/// операции с материалами дел — не настройка контура, и «пока Администратора нет — можно всем» здесь
/// означало бы поиск по лицам без роли. Fail-closed: нет роли — нет права.
/// </remarks>
public sealed class MediaAdministration(IUserRoleStore roles, ISubjectProvider subjectProvider) : IMediaAdministration
{
    /// <inheritdoc />
    public Task<bool> CanUploadAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerHasRoleAsync(
            roles, subjectProvider, cancellationToken,
            InvestigationRole.Investigator, InvestigationRole.Administrator, InvestigationRole.Head);

    /// <inheritdoc />
    public Task<bool> CanSearchAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerHasRoleAsync(
            roles, subjectProvider, cancellationToken,
            InvestigationRole.Investigator, InvestigationRole.FaceExpert, InvestigationRole.Administrator, InvestigationRole.Head);

    /// <inheritdoc />
    public Task<bool> CanPurgeAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerHasRoleAsync(
            roles, subjectProvider, cancellationToken,
            InvestigationRole.Administrator, InvestigationRole.Head);
}

/// <summary>
/// Реализация порта пакета «Медиа» <see cref="IVerificationPolicy"/>: кто вправе выступать на стадии
/// верификации (ТП-004). Стадия эксперта — роль «Эксперт по лицам», стадия верификатора — роль
/// «Верификатор». Само правило двух лиц (<see cref="TwoPersonRule"/>) — модуля и профилем не ослабляется.
/// </summary>
/// <remarks>
/// ТБ-073: Администратор НЕ выступает ни экспертом, ни верификатором — иначе одна учётка с высшими
/// правами закрывала бы обе стадии, и «два независимых сотрудника» превращались бы в формальность.
/// Руководитель утверждает результат (ТФ-ВЕР-02), но решений стадий тоже не пишет.
/// </remarks>
public sealed class VerificationPolicy(IUserRoleStore roles) : IVerificationPolicy
{
    /// <inheritdoc />
    public async Task<bool> CanActAsync(VerificationStage stage, int userId, CancellationToken cancellationToken = default)
    {
        var role = await roles.GetRoleAsync(userId, cancellationToken);
        return stage switch
        {
            VerificationStage.Expert => role == InvestigationRole.FaceExpert,
            VerificationStage.Verifier => role == InvestigationRole.Verifier,
            _ => false,
        };
    }
}
