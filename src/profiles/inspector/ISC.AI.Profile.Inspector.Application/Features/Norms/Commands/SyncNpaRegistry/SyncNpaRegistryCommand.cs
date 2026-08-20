using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>
/// Синхронизировать картотеку с корпусом: по метаданным ЦБД (documentCode/editionId) создать нормы,
/// редакции и связки; прежние автоматические редакции актов с новой редакцией — погасить (GATE-3).
/// Идемпотентна — повторный запуск ничего не меняет. Запускается кнопкой с картотеки и фоном после
/// импорта пакета.
/// </summary>
public sealed record SyncNpaRegistryCommand : IRequest<ResponseDto<NpaSyncResult>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => "inspector:norms:sync-from-corpus";

    /// <inheritdoc cref="SyncNpaRegistryCommand" />
    public sealed class Handler(INpaRegistrySynchronizer synchronizer, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SyncNpaRegistryCommand, ResponseDto<NpaSyncResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<NpaSyncResult>> Handle(
            SyncNpaRegistryCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await NormGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<NpaSyncResult>.BadRequest(NormGuard.Denied);
            }

            var result = await synchronizer.SyncFromCorpusAsync(cancellationToken);
            return ResponseDto<NpaSyncResult>.Ok(result,
                $"Просмотрено документов: {result.Scanned}; норм создано: {result.NormsCreated}, "
                + $"редакций: {result.RevisionsCreated}, привязано документов: {result.DocumentsLinked} "
                + $"(фрагментов: {result.ChunksLinked}); погашено прежних редакций: {result.RevisionsRepealed}.");
        }
    }
}
