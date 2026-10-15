using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Search;

/// <summary>
/// История единого поиска вошедшего сотрудника (ADR-0049): закреплённые, затем недавние — в пределах его ТЕКУЩЕГО
/// допуска. Без определённого сотрудника — пустая история.
/// </summary>
/// <remarks>
/// НЕ <see cref="IAuditableRequest"/> — намеренно: это собственные запросы сотрудника, а не обращение к материалам дел;
/// каждый сам поиск уже записан в журнал (ADR-0048), повтор из истории — новый поиск и новая запись.
/// Fail-closed (ТБ-021): без контекста допуска чтения нет.
/// </remarks>
public sealed record ListSearchHistoryQuery : IRequest<ResponseDto<IReadOnlyList<SearchHistoryRow>>>
{
    /// <inheritdoc cref="ListSearchHistoryQuery" />
    public sealed class Handler(ISearchHistoryStore history, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<ListSearchHistoryQuery, ResponseDto<IReadOnlyList<SearchHistoryRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<SearchHistoryRow>>> Handle(
            ListSearchHistoryQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<IReadOnlyList<SearchHistoryRow>>.Ok([], 0);
            }

            var rows = await history.ListAsync(userId, access.MaxClassification, cancellationToken);
            return ResponseDto<IReadOnlyList<SearchHistoryRow>>.Ok(rows, rows.Count);
        }
    }
}

/// <summary>Закрепить (★) или открепить свой запрос из истории (ADR-0049); закреплённый не вытесняется новыми.</summary>
/// <remarks>Журнал (ТБ-030): изменение собственной истории — запись <see cref="AuditAction.Modify"/> без искомого текста (ТБ-032).</remarks>
/// <param name="EntryId">Запрос из истории.</param>
/// <param name="Pinned">Закрепить или открепить.</param>
public sealed record PinSearchHistoryCommand(int EntryId, bool Pinned) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <summary>Отказ: закреплённых уже предельное число.</summary>
    public static readonly string PinLimitMessage = string.Create(
        CultureInfo.InvariantCulture,
        $"Закрепить можно не больше {ISearchHistoryStore.MaxPinned} запросов — открепите ненужные.");

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"investigation:search-history:{EntryId.ToString(CultureInfo.InvariantCulture)}:{(Pinned ? "pin" : "unpin")}";

    /// <inheritdoc cref="PinSearchHistoryCommand" />
    public sealed class Handler(ISearchHistoryStore history, ISubjectProvider subjectProvider)
        : IRequestHandler<PinSearchHistoryCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(PinSearchHistoryCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<bool>.NotFound(SearchHistoryMessages.NotFound);
            }

            return await history.SetPinnedAsync(userId, command.EntryId, command.Pinned, cancellationToken) switch
            {
                SearchHistoryWriteResult.Ok => ResponseDto<bool>.Ok(true),
                SearchHistoryWriteResult.PinLimit => ResponseDto<bool>.BadRequest(PinLimitMessage),
                _ => ResponseDto<bool>.NotFound(SearchHistoryMessages.NotFound),
            };
        }
    }
}

/// <summary>Удалить свой запрос из истории (ADR-0049). Журнал аудита поиска этим не меняется (ТБ-030).</summary>
/// <param name="EntryId">Запрос из истории.</param>
public sealed record DeleteSearchHistoryCommand(int EntryId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:search-history:{EntryId.ToString(CultureInfo.InvariantCulture)}:delete";

    /// <inheritdoc cref="DeleteSearchHistoryCommand" />
    public sealed class Handler(ISearchHistoryStore history, ISubjectProvider subjectProvider)
        : IRequestHandler<DeleteSearchHistoryCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DeleteSearchHistoryCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId
                || await history.DeleteAsync(userId, command.EntryId, cancellationToken) != SearchHistoryWriteResult.Ok)
            {
                return ResponseDto<bool>.NotFound(SearchHistoryMessages.NotFound);
            }

            return ResponseDto<bool>.Ok(true);
        }
    }
}

/// <summary>Очистить свою историю поиска — закреплённые запросы остаются (ADR-0049).</summary>
public sealed record ClearSearchHistoryCommand : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => "investigation:search-history:clear";

    /// <inheritdoc cref="ClearSearchHistoryCommand" />
    public sealed class Handler(ISearchHistoryStore history, ISubjectProvider subjectProvider)
        : IRequestHandler<ClearSearchHistoryCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(ClearSearchHistoryCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<int>.Ok(0, 0);
            }

            var removed = await history.ClearAsync(userId, cancellationToken);
            return ResponseDto<int>.Ok(removed, removed);
        }
    }
}

/// <summary>Сообщения истории поиска.</summary>
public static class SearchHistoryMessages
{
    /// <summary>Единый ответ на «нет» и «чужое» (ТБ-021).</summary>
    public const string NotFound = "Запрос в истории не найден.";
}
