using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Comments;

/// <summary>
/// Сценарии комментариев к документу (ТЗ СКИД §4.8). КАЖДЫЙ сценарий сперва проверяет допуск к САМОМУ
/// документу через <c>IDocumentStore.GetAsync</c> (решётка гриф/подразделение + построчная политика роли,
/// этапы 6.1/6.4): комментарии к недоступному документу неотличимы от «документа нет» — иначе лента
/// комментариев стала бы обходным каналом чтения закрытого документа.
/// </summary>

/// <summary>Лента комментариев документа (§4.8).</summary>
/// <param name="IncludeResolved">
/// Показывать закрытые обсуждения. Отсев выполняет хранилище — В ЗАПРОСЕ, а не в разметке.
/// </param>
public sealed record ListCommentsQuery(int DocumentId, bool IncludeResolved = true)
    : IRequest<ResponseDto<IReadOnlyList<CommentItem>>>
{
    /// <inheritdoc cref="ListCommentsQuery" />
    public sealed class Handler(
        ICommentStore comments, IDocumentStore documents, IAccessContextProvider accessProvider)
        : IRequestHandler<ListCommentsQuery, ResponseDto<IReadOnlyList<CommentItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CommentItem>>> Handle(
            ListCommentsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (await documents.GetAsync(query.DocumentId, access, cancellationToken) is null)
            {
                return ResponseDto<IReadOnlyList<CommentItem>>.NotFound("Документ не найден.");
            }

            var items = await comments.ListAsync(
                query.DocumentId, query.IncludeResolved, cancellationToken);
            return ResponseDto<IReadOnlyList<CommentItem>>.Ok(items, items.Count);
        }
    }
}
