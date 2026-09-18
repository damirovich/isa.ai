using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Реализация порта пакета «Медиа» <see cref="IMediaAdministration"/>: кто вправе загружать носители,
/// искать по лицу и гарантированно удалять. Матрица — СТРОГО по ТП-004, без расширений (ADR-0022, п. 8):
/// загрузка — Следователь; поиск — Следователь (инициирует) и Эксперт по лицам (проводит);
/// гарантированное удаление — Администратор и Руководитель. Право — по РОЛИ текущего субъекта; решётка
/// гриф/подразделение (ТБ-020) применяется модулем отдельно и всегда — эти проверки поверх неё.
/// </summary>
/// <remarks>
/// Почему Руководитель и Администратор по лицу НЕ ищут и носители НЕ загружают: каждое обращение к
/// биометрии аудируется как поиск в деле по основанию (ТБ-072) — это действие процессуальной роли.
/// Руководитель по ТП-004 видит дела подразделения и утверждает результаты, а не ищет; Администратор
/// ведёт учётные записи, допуски, справочники и модели и к биометрическим материалам не обращается.
/// Удаление носителя (регламент закрытия дела с актом) — напротив, административное действие обоих.
/// Матрица закреплена таблицей в тесте по всем ролям и «без роли»: изменение требует правки теста и ADR.
///
/// БЕЗ режима первичной настройки (в отличие от <see cref="AdministrationRule.CallerCanManageAsync"/>):
/// операции с материалами дел — не настройка контура, и «пока Администратора нет — можно всем» здесь
/// означало бы поиск по лицам без роли. Fail-closed: нет роли — нет права (ТБ-012).
/// </remarks>
public sealed class MediaAdministration(IUserRoleStore roles, ISubjectProvider subjectProvider) : IMediaAdministration
{
    /// <inheritdoc />
    public Task<bool> CanUploadAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerHasRoleAsync(
            roles, subjectProvider, cancellationToken,
            InvestigationRole.Investigator);

    /// <inheritdoc />
    public Task<bool> CanSearchAsync(CancellationToken cancellationToken = default) =>
        AdministrationRule.CallerHasRoleAsync(
            roles, subjectProvider, cancellationToken,
            InvestigationRole.Investigator, InvestigationRole.FaceExpert);

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
