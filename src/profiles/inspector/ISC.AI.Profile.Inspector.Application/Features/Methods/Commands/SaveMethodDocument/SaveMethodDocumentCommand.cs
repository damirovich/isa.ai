using System.Text.Json;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>
/// Сохранить сформированную методику в реестр (§5.2.9, Ц-03 «институциональная память»).
/// Сохраняется всегда ЧЕРНОВИКОМ (ТБ-042 — утверждает человек отдельным действием); гриф —
/// из результата генерации (максимум грифов использованных фрагментов), им же классифицируется
/// запись аудита (ТБ-032).
/// </summary>
public sealed record SaveMethodDocumentCommand(
    string ArtifactKind,
    string InspectionType,
    string Scope,
    string Body,
    IReadOnlyList<CitationCheck>? Citations,
    bool AllCitationsConfirmed,
    short Classification) : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:method:save:{ArtifactKind}:{Scope}";

    /// <inheritdoc />
    public short? AuditClassification => Classification;

    /// <inheritdoc cref="SaveMethodDocumentCommand" />
    public sealed class Handler(IMethodRegistryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SaveMethodDocumentCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            SaveMethodDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await MethodRegistryGuard.CallerCanSaveAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(MethodRegistryGuard.SaveDenied);
            }

            var methodId = await store.SaveAsync(
                new MethodDocumentDraft(
                    command.ArtifactKind, command.InspectionType, command.Scope, command.Body,
                    command.Citations is { Count: > 0 } citations ? JsonSerializer.Serialize(citations) : null,
                    command.AllCitationsConfirmed,
                    command.Classification,
                    await subjectProvider.GetCurrentUserIdAsync(cancellationToken)),
                cancellationToken);
            return ResponseDto<int>.Ok(methodId);
        }
    }
}
