using System.Globalization;
using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.CaseChat;

/// <summary>Тексты и пределы чата по материалам дел (ADR-0051).</summary>
public static class CaseChatMessages
{
    /// <summary>Единый ответ «нет или нельзя» (ТБ-021).</summary>
    public const string NotFound = "Дело или диалог не найдены либо недоступны.";

    /// <summary>Нет права «Чат по материалам дел» матрицы доступа.</summary>
    public const string NoRight = "Чат по материалам дел недоступен вашей роли (матрица доступа).";

    /// <summary>В области нет ни одного материала с текстом в корпусе.</summary>
    public const string NoMaterials =
        "В доступных материалах пока нечего читать: прикрепите документы к делу, дождитесь расшифровки записей или заведите сводку.";

    /// <summary>Нет ни одного доступного дела.</summary>
    public const string NoCases = "У вас нет доступных дел — спрашивать не по чему.";

    /// <summary>Пустой вопрос.</summary>
    public const string EmptyQuestion = "Введите вопрос.";

    /// <summary>Узел ИИ не ответил.</summary>
    public const string ModelUnavailable = "Узел ИИ недоступен или не ответил вовремя — попробуйте позже.";

    /// <summary>Сколько фрагментов подаётся модели на один вопрос.</summary>
    public const int TopK = 10;

    /// <summary>Сколько последних реплик диалога подаётся модели как история.</summary>
    public const int HistoryTurns = 6;

    /// <summary>Наибольшая длина одной реплики истории, подаваемой модели.</summary>
    public const int HistoryTurnLength = 1_500;

    /// <summary>Сколько фрагментов выдаёт поиск по смыслу.</summary>
    public const int SenseSearchTopK = 20;
}

/// <summary>Вкладка или страница чата: свои диалоги области и готовность материалов.</summary>
/// <param name="Chats">Свои диалоги, новые сверху.</param>
/// <param name="Coverage">Что видит чат.</param>
public sealed record CaseChatOverview(IReadOnlyList<CaseChatSummary> Chats, ChatCoverage Coverage);

/// <summary>
/// Свои диалоги по делу (или по всем доступным делам при <c>CaseId = null</c>) и готовность материалов (ADR-0051).
/// Свой список — без записи в журнал; материалы не синхронизируются (только строка готовности).
/// </summary>
/// <param name="CaseId">Дело или <see langword="null"/> — все доступные дела.</param>
public sealed record ListCaseChatsQuery(int? CaseId) : IRequest<ResponseDto<CaseChatOverview>>
{
    /// <inheritdoc cref="ListCaseChatsQuery" />
    public sealed class Handler(
        ICaseChatStore chats,
        ChatMaterialResolver materials,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider) : IRequestHandler<ListCaseChatsQuery, ResponseDto<CaseChatOverview>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CaseChatOverview>> Handle(ListCaseChatsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await PermissionRule.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesChat, cancellationToken))
            {
                return ResponseDto<CaseChatOverview>.BadRequest(CaseChatMessages.NoRight);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var list = await chats.ListAsync(query.CaseId, access, cancellationToken);
            var area = list is null ? null : await ChatArea.ResolveAsync(chats, query.CaseId, access, cancellationToken);
            if (list is null || area is null)
            {
                return ResponseDto<CaseChatOverview>.NotFound(CaseChatMessages.NotFound);
            }

            var resolved = await materials.ResolveAsync(area, access, sync: false, cancellationToken);
            return ResponseDto<CaseChatOverview>.Ok(new CaseChatOverview(list, resolved.Coverage));
        }
    }
}

/// <summary>Дела, по которым сотрудник может спрашивать, — для выбора области на странице чата (ADR-0051).</summary>
public sealed record ListChatScopesQuery : IRequest<ResponseDto<IReadOnlyList<CaseChatScope>>>
{
    /// <inheritdoc cref="ListChatScopesQuery" />
    public sealed class Handler(
        ICaseChatStore chats,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider) : IRequestHandler<ListChatScopesQuery, ResponseDto<IReadOnlyList<CaseChatScope>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CaseChatScope>>> Handle(ListChatScopesQuery query, CancellationToken cancellationToken)
        {
            if (!await PermissionRule.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesChat, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<CaseChatScope>>.BadRequest(CaseChatMessages.NoRight);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var scopes = await chats.ListScopesAsync(access, cancellationToken);
            return ResponseDto<IReadOnlyList<CaseChatScope>>.Ok(scopes, scopes.Count);
        }
    }
}

/// <summary>Свой диалог с сообщениями и источниками (ADR-0051). Чтение переписки по делам — запись в журнал.</summary>
/// <param name="ChatId">Диалог.</param>
public sealed record GetCaseChatQuery(int ChatId) : IRequest<ResponseDto<CaseChatThread>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case-chat:{ChatId.ToString(CultureInfo.InvariantCulture)}:view";

    /// <inheritdoc cref="GetCaseChatQuery" />
    public sealed class Handler(
        ICaseChatStore chats,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider) : IRequestHandler<GetCaseChatQuery, ResponseDto<CaseChatThread>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CaseChatThread>> Handle(GetCaseChatQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await PermissionRule.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesChat, cancellationToken))
            {
                return ResponseDto<CaseChatThread>.BadRequest(CaseChatMessages.NoRight);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return await chats.GetAsync(query.ChatId, access, cancellationToken) is { } thread
                ? ResponseDto<CaseChatThread>.Ok(thread)
                : ResponseDto<CaseChatThread>.NotFound(CaseChatMessages.NotFound);
        }
    }
}

/// <summary>Готовый ответ чата: сохранён в диалоге вместе с источниками и итогом проверки ссылок.</summary>
/// <param name="Text">Текст ответа.</param>
/// <param name="Classification">Гриф ответа.</param>
/// <param name="Sources">Фрагменты, поданные модели, с материалами дел.</param>
/// <param name="Check">Итог проверки ссылок.</param>
public sealed record CaseChatAnswer(string Text, short Classification, IReadOnlyList<CaseChatSource> Sources, CaseChatCheck Check);

/// <summary>
/// Обновление потока ответа. Первое — <see cref="ChatId"/> (диалог, куда записан вопрос), затем черновик кусками
/// (<see cref="TextDelta"/>), последнее — <see cref="Final"/> или <see cref="Error"/>.
/// </summary>
/// <param name="ChatId">Диалог (в первом обновлении).</param>
/// <param name="TextDelta">Кусок черновика — НЕпроверенный текст, пока не пришёл итог.</param>
/// <param name="Final">Итог: проверенный ответ с источниками.</param>
/// <param name="Error">Отказ или сбой — текст для сотрудника.</param>
public sealed record CaseChatUpdate(int? ChatId = null, string? TextDelta = null, CaseChatAnswer? Final = null, string? Error = null);

/// <summary>
/// Вопрос к материалам дела или всех доступных дел (ТФ-ДДЛ-02, ADR-0051): поиск фрагментов только в материалах области —
/// документы дел, расшифровки записей, сводки и справки, сведения дел, — ответ локальной модели по ним со ссылками,
/// проверка ссылок, сохранение в диалоге. Ответ отдаётся потоком.
/// </summary>
/// <remarks>
/// <para>ИНВАРИАНТЫ. Область — дела под решёткой и их материалы под решётками документооборота и пакета «Медиа»
/// (<see cref="ChatMaterialResolver"/>); поверх неё ядро применяет floor допуска к фрагментам (ТБ-020). Системное правило
/// грунтовки ядра идёт первым, промпт профиля — вторым и его не ослабляет (ТБ-041). Ссылки ответа проверяет ядровой
/// валидатор по поданным фрагментам (ТБ-040, инвариант 1), ссылки на номера фрагментов — <see cref="FragmentReferences"/>;
/// непроверенное показывается с предупреждением, ответ — черновик для человека (ТБ-042).</para>
/// <para>АУДИТ (ТБ-030): до обращения к модели — запись <see cref="AuditAction.Search"/> с областью, диалогом и вопросом;
/// саму генерацию с фрагментами и ответом пишет ядро (<see cref="AuditAction.Generate"/>). Недоступен журнал — модель не
/// вызывается (fail-closed).</para>
/// </remarks>
/// <param name="CaseId">Дело или <see langword="null"/> — все доступные дела.</param>
/// <param name="ChatId">Диалог или <see langword="null"/> — начать новый.</param>
/// <param name="Text">Вопрос.</param>
public sealed record SendCaseChatMessageCommand(int? CaseId, int? ChatId, string Text) : IStreamRequest<CaseChatUpdate>
{
    /// <inheritdoc cref="SendCaseChatMessageCommand" />
    public sealed class Handler(
        ICaseChatStore chats,
        ChatMaterialResolver materials,
        IGroundedGenerator generator,
        IAuditWriter auditWriter,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider) : IStreamRequestHandler<SendCaseChatMessageCommand, CaseChatUpdate>
    {
        /// <inheritdoc />
        public async IAsyncEnumerable<CaseChatUpdate> Handle(
            SendCaseChatMessageCommand command, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var text = (command.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                yield return new CaseChatUpdate(Error: CaseChatMessages.EmptyQuestion);
                yield break;
            }

            if (text.Length > CaseChatLimits.MaxQuestionLength)
            {
                text = text[..CaseChatLimits.MaxQuestionLength];
            }

            if (!await PermissionRule.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesChat, cancellationToken))
            {
                yield return new CaseChatUpdate(Error: CaseChatMessages.NoRight);
                yield break;
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var area = await ChatArea.ResolveAsync(chats, command.CaseId, access, cancellationToken);
            if (area is null)
            {
                yield return new CaseChatUpdate(Error: CaseChatMessages.NotFound);
                yield break;
            }

            if (area.Cases.Count == 0)
            {
                yield return new CaseChatUpdate(Error: CaseChatMessages.NoCases);
                yield break;
            }

            // Материалы области; сведения дел и сводки синхронизируются с корпусом — может понадобиться модель эмбеддингов.
            ChatMaterials scope;
            var unavailable = false;
            try
            {
                scope = await materials.ResolveAsync(area, access, sync: true, cancellationToken);
            }
            catch (ModelUnavailableException)
            {
                unavailable = true;
                scope = ChatMaterials.Empty;
            }

            if (unavailable)
            {
                yield return new CaseChatUpdate(Error: CaseChatMessages.ModelUnavailable);
                yield break;
            }

            if (scope.Items.Count == 0)
            {
                yield return new CaseChatUpdate(Error: CaseChatMessages.NoMaterials);
                yield break;
            }

            // Диалог: новый (заголовок — начало вопроса) или свой диалог ЭТОЙ области.
            IReadOnlyList<ChatTurn> history = [];
            int chatId;
            if (command.ChatId is { } existing)
            {
                var thread = await chats.GetAsync(existing, access, cancellationToken);
                if (thread is null || thread.CaseId != command.CaseId)
                {
                    yield return new CaseChatUpdate(Error: CaseChatMessages.NotFound);
                    yield break;
                }

                chatId = thread.Id;
                history = thread.Messages
                    .TakeLast(CaseChatMessages.HistoryTurns)
                    .Select(m => new ChatTurn(m.Role, m.Text.Length <= CaseChatMessages.HistoryTurnLength ? m.Text : m.Text[..CaseChatMessages.HistoryTurnLength]))
                    .ToList();
            }
            else if (await chats.CreateAsync(command.CaseId, text, access, cancellationToken) is { } created)
            {
                chatId = created;
            }
            else
            {
                yield return new CaseChatUpdate(Error: CaseChatMessages.NotFound);
                yield break;
            }

            // Вопрос по делу — с грифом дела; по всем делам — без грифа (это слова сотрудника, а не материал).
            var questionClassification = area.Single?.Classification ?? (short)0;
            if (!await chats.AppendAsync(chatId, new CaseChatMessageDraft(ConversationMessageRole.User, text, questionClassification), access, cancellationToken))
            {
                yield return new CaseChatUpdate(Error: CaseChatMessages.NotFound);
                yield break;
            }

            // АУДИТ ДО МОДЕЛИ (ТБ-030, fail-closed): нет записи — нет обращения к материалам.
            var inv = CultureInfo.InvariantCulture;
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Search,
                    area.MaxClassification,
                    SubjectId: access.NumericSubjectId,
                    ObjectRef: area.Single is { } single
                        ? "investigation:case:" + single.CaseId.ToString(inv) + ":chat:" + chatId.ToString(inv)
                        : "investigation:chat:" + chatId.ToString(inv),
                    DivisionId: area.Single?.DivisionId,
                    PayloadSensitive: $"Вопрос к материалам {area.Describe()} (материалов в области: {scope.Items.Count.ToString(inv)}): {text}"),
                cancellationToken);

            yield return new CaseChatUpdate(ChatId: chatId);

            var request = new GroundedRequest(
                text,
                ModelRole.Draft,
                CaseChatMessages.TopK,
                CaseChatPrompt.Render(area.Describe()),
                history,
                new RetrievalFilter(DocumentIds: scope.CoreDocumentIds));

            await using var stream = generator.GenerateStreamingAsync(request, access, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                GroundedStreamUpdate update;
                var failed = false;
                try
                {
                    if (!await stream.MoveNextAsync())
                    {
                        break;
                    }

                    update = stream.Current;
                }
                catch (ModelUnavailableException)
                {
                    // Попытку с уже извлечёнными фрагментами ядро записало в журнал; вопрос остался в диалоге.
                    failed = true;
                    update = null!;
                }

                if (failed)
                {
                    yield return new CaseChatUpdate(ChatId: chatId, Error: CaseChatMessages.ModelUnavailable + " Вопрос сохранён в диалоге.");
                    yield break;
                }

                if (update.Final is { } final)
                {
                    var answer = await SaveAnswerAsync(chatId, area, scope, final, access, cancellationToken);
                    yield return new CaseChatUpdate(ChatId: chatId, Final: answer);
                    yield break;
                }

                if (!string.IsNullOrEmpty(update.TextDelta))
                {
                    yield return new CaseChatUpdate(TextDelta: update.TextDelta);
                }
            }
        }

        // Источники — фрагменты в порядке подачи модели (номер = «[Фрагмент N]»); проверка — вердикт ядра + номера.
        private async Task<CaseChatAnswer> SaveAnswerAsync(
            int chatId, ChatArea area, ChatMaterials scope, GroundedResponse final, AccessContext access, CancellationToken cancellationToken)
        {
            var sources = final.UsedFragments
                .Select((fragment, index) => ChatSources.From(index + 1, fragment, scope))
                .OfType<CaseChatSource>()
                .ToList();
            var check = new CaseChatCheck(
                final.Grounding.Citations.Where(c => c.Status != CitationStatus.Confirmed).Select(c => c.Citation).Distinct().ToList(),
                FragmentReferences.Unknown(final.Answer, final.UsedFragments.Count));
            var classification = Math.Max(final.ResultClassification, area.Single?.Classification ?? (short)0);
            var answer = new CaseChatAnswer(final.Answer, (short)classification, sources, check);

            await chats.AppendAsync(
                chatId,
                new CaseChatMessageDraft(ConversationMessageRole.Assistant, final.Answer, answer.Classification, sources, check),
                access,
                cancellationToken);
            return answer;
        }
    }
}

/// <summary>Источник ответа из фрагмента корпуса (ADR-0051).</summary>
public static class ChatSources
{
    /// <summary>Источник по фрагменту; <see langword="null"/> — фрагмент не из материалов области (такого быть не должно).</summary>
    public static CaseChatSource? From(int number, RetrievedChunk fragment, ChatMaterials scope)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(scope);
        return scope.ByCoreId(fragment.DocumentId) is { } material
            ? new CaseChatSource(
                number,
                material.Kind,
                material.TargetId,
                material.CaseId,
                material.Label,
                fragment.ChunkId,
                fragment.Classification,
                Excerpt(fragment.Text),
                material.Kind == ChatSourceKind.Transcript ? TranscriptCorpusText.FirstTimecodeMs(fragment.Text) : null)
            : null;
    }

    /// <summary>Начало текста фрагмента в одну строку, не длиннее <see cref="CaseChatLimits.MaxExcerptLength"/>.</summary>
    public static string Excerpt(string? text)
    {
        var value = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length <= CaseChatLimits.MaxExcerptLength ? value : value[..(CaseChatLimits.MaxExcerptLength - 1)] + "…";
    }
}

/// <summary>Переименовать свой диалог.</summary>
/// <param name="ChatId">Диалог.</param>
/// <param name="Title">Новое название.</param>
public sealed record RenameCaseChatCommand(int ChatId, string Title) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case-chat:{ChatId.ToString(CultureInfo.InvariantCulture)}:rename";

    /// <inheritdoc cref="RenameCaseChatCommand" />
    public sealed class Handler(ICaseChatStore chats, IAccessContextProvider accessProvider) : IRequestHandler<RenameCaseChatCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RenameCaseChatCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return await chats.RenameAsync(command.ChatId, command.Title, access, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound(CaseChatMessages.NotFound);
        }
    }
}

/// <summary>Удалить свой диалог: переписка удаляется физически, записи журнала остаются (ТБ-030).</summary>
/// <param name="ChatId">Диалог.</param>
public sealed record DeleteCaseChatCommand(int ChatId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:case-chat:{ChatId.ToString(CultureInfo.InvariantCulture)}:delete";

    /// <inheritdoc cref="DeleteCaseChatCommand" />
    public sealed class Handler(ICaseChatStore chats, IAccessContextProvider accessProvider) : IRequestHandler<DeleteCaseChatCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DeleteCaseChatCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            return await chats.DeleteAsync(command.ChatId, access, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound(CaseChatMessages.NotFound);
        }
    }
}

/// <summary>Найденный по смыслу фрагмент материала.</summary>
/// <param name="Source">Источник (вид, что открыть, дело, подпись, момент записи).</param>
/// <param name="Text">Текст фрагмента.</param>
/// <param name="Similarity">Близость к запросу, 0…1 (1 — совпадение по смыслу).</param>
public sealed record CaseSenseHit(CaseChatSource Source, string Text, double Similarity);

/// <summary>Итог поиска по смыслу.</summary>
/// <param name="Hits">Фрагменты, ближайшие первыми.</param>
/// <param name="Materials">Материалов в области поиска.</param>
public sealed record CaseSenseSearchResult(IReadOnlyList<CaseSenseHit> Hits, int Materials);

/// <summary>
/// Поиск по смыслу в материалах дела или всех доступных дел (ТФ-ДДЛ-03, ADR-0051): тем же ретривером ядра и в той же
/// области, что чат, но без модели — сотрудник видит сами фрагменты. Одна запись журнала <see cref="AuditAction.Search"/>
/// с текстом запроса.
/// </summary>
/// <param name="CaseId">Дело или <see langword="null"/> — все доступные дела.</param>
/// <param name="Text">Что искать (смысл, а не точные слова).</param>
public sealed record SearchCaseDocumentsQuery(int? CaseId, string Text) : IRequest<ResponseDto<CaseSenseSearchResult>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary =>
        (CaseId is { } id ? $"investigation:case:{id.ToString(CultureInfo.InvariantCulture)}:materials:sense-search" : "investigation:materials:sense-search")
        + $";text={Clip(Text)}";

    private static string Clip(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        return value.Length <= CaseChatLimits.MaxQuestionLength ? value : value[..CaseChatLimits.MaxQuestionLength];
    }

    /// <inheritdoc cref="SearchCaseDocumentsQuery" />
    public sealed class Handler(
        ICaseChatStore chats,
        ChatMaterialResolver materials,
        IRetriever retriever,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider,
        IAccessContextProvider accessProvider) : IRequestHandler<SearchCaseDocumentsQuery, ResponseDto<CaseSenseSearchResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CaseSenseSearchResult>> Handle(SearchCaseDocumentsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var text = Clip(query.Text);
            if (text.Length == 0)
            {
                return ResponseDto<CaseSenseSearchResult>.BadRequest(CaseChatMessages.EmptyQuestion);
            }

            if (!await PermissionRule.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesChat, cancellationToken))
            {
                return ResponseDto<CaseSenseSearchResult>.BadRequest(CaseChatMessages.NoRight);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (await ChatArea.ResolveAsync(chats, query.CaseId, access, cancellationToken) is not { } area)
            {
                return ResponseDto<CaseSenseSearchResult>.NotFound(CaseChatMessages.NotFound);
            }

            try
            {
                var scope = await materials.ResolveAsync(area, access, sync: true, cancellationToken);
                if (scope.Items.Count == 0)
                {
                    return ResponseDto<CaseSenseSearchResult>.Ok(new CaseSenseSearchResult([], 0));
                }

                var fragments = await retriever.RetrieveAsync(
                    text, access, CaseChatMessages.SenseSearchTopK, new RetrievalFilter(DocumentIds: scope.CoreDocumentIds), cancellationToken);
                var hits = fragments
                    .Select((f, index) => ChatSources.From(index + 1, f, scope) is { } source
                        ? new CaseSenseHit(source, f.Text, Math.Clamp(1 - f.Score, 0, 1))
                        : null)
                    .OfType<CaseSenseHit>()
                    .ToList();
                return ResponseDto<CaseSenseSearchResult>.Ok(new CaseSenseSearchResult(hits, scope.Items.Count), hits.Count);
            }
            catch (ModelUnavailableException)
            {
                return ResponseDto<CaseSenseSearchResult>.BadRequest("Узел ИИ (модель поиска по смыслу) недоступен — попробуйте позже.");
            }
        }
    }
}
