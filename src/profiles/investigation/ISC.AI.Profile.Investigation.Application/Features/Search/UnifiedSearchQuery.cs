using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Search;

/// <summary>Виды выдачи единого поиска (ADR-0048); можно сочетать.</summary>
[Flags]
public enum SearchKinds
{
    /// <summary>Ничего.</summary>
    None = 0,

    /// <summary>Дела: номер, № задания, название.</summary>
    Cases = 1,

    /// <summary>Фигуранты: ФИО, псевдоним, дата или год рождения.</summary>
    Persons = 2,

    /// <summary>Автотранспорт: госномер, марка, модель.</summary>
    Vehicles = 4,

    /// <summary>Адреса фигурантов и место жительства из анкеты.</summary>
    Addresses = 8,

    /// <summary>Сводки и справки: текст полей бланка.</summary>
    Reports = 16,

    /// <summary>Расшифровки речи носителей дел.</summary>
    Transcripts = 32,

    /// <summary>Документы дел: регистрационный номер, вид, краткое содержание.</summary>
    Documents = 64,

    /// <summary>Все виды.</summary>
    All = Cases | Persons | Vehicles | Addresses | Reports | Transcripts | Documents,
}

/// <summary>Найденный фрагмент расшифровки — с делом носителя (для перехода к месту записи, ТФ-ПСК-02).</summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="CaseId">Дело носителя (первая привязка среди дел области поиска).</param>
/// <param name="AssetFileName">Исходное имя файла.</param>
/// <param name="Kind">Аудио или видео.</param>
/// <param name="SegmentIndex">Номер фрагмента.</param>
/// <param name="StartMs">Начало фрагмента, мс — место записи.</param>
/// <param name="Text">Текст фрагмента дословно.</param>
public sealed record TranscriptSearchHit(int AssetId, int CaseId, string AssetFileName, MediaKind Kind, int SegmentIndex, long StartMs, string Text);

/// <summary>Найденный документ дела (документооборот).</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="CaseId">Дело, к которому он привязан.</param>
/// <param name="RegNumber">Регистрационный номер (у черновика — нет).</param>
/// <param name="RegDate">Дата регистрации.</param>
/// <param name="TypeName">Вид документа.</param>
/// <param name="ShortContent">Краткое содержание.</param>
public sealed record DocumentSearchHit(int DocumentId, int CaseId, string? RegNumber, DateOnly RegDate, string TypeName, string ShortContent);

/// <summary>Выдача единого поиска: разделы по видам (не больше <see cref="UnifiedSearchQuery.Limit"/> в каждом).</summary>
/// <param name="Text">Запрос, как искали (обрезанный).</param>
/// <param name="Cases">Дела.</param>
/// <param name="Persons">Фигуранты.</param>
/// <param name="Vehicles">Автотранспорт.</param>
/// <param name="Addresses">Адреса.</param>
/// <param name="Reports">Сводки и справки.</param>
/// <param name="Transcripts">Фрагменты расшифровок.</param>
/// <param name="Documents">Документы дел.</param>
/// <param name="CaseNumbers">Номера дел области поиска — подпись «дело № …» у строк.</param>
/// <param name="Truncated">Виды, в которых найдено больше предела — показаны первые.</param>
public sealed record UnifiedSearchResult(
    string Text,
    IReadOnlyList<CaseRow> Cases,
    IReadOnlyList<PersonRow> Persons,
    IReadOnlyList<VehicleSearchRow> Vehicles,
    IReadOnlyList<AddressSearchRow> Addresses,
    IReadOnlyList<CaseReportRow> Reports,
    IReadOnlyList<TranscriptSearchHit> Transcripts,
    IReadOnlyList<DocumentSearchHit> Documents,
    IReadOnlyDictionary<int, string> CaseNumbers,
    SearchKinds Truncated)
{
    /// <summary>Всего строк выдачи по всем видам.</summary>
    public int TotalCount =>
        Cases.Count + Persons.Count + Vehicles.Count + Addresses.Count + Reports.Count + Transcripts.Count + Documents.Count;
}

/// <summary>
/// Единый поиск по материалам дел (ТФ-ПСК-01, ADR-0048): одна строка — дела, фигуранты, автотранспорт, адреса,
/// сводки и справки, расшифровки и документы дел, в пределах дел субъекта.
/// </summary>
/// <remarks>
/// <para>РЕЖИМ (ТБ-020/021, ТБ-071): область — дела, доступные субъекту по полной решётке и роли (тот же список, что
/// реестр дел), при отделе — только дела отдела (ADR-0039, отбор, а не граница доступа). Всё остальное ищется ТОЛЬКО в
/// этих делах, и каждое хранилище повторно применяет свою решётку на стороне БД (фигурант, строка адреса и
/// транспорта, сводка, фрагмент расшифровки, документ — у каждой строки свои гриф и подразделение). Недоступное в
/// выдаче не появляется ничем, даже числом. Без контекста доступа — отказ (fail-closed, ТБ-021).</para>
/// <para>АУДИТ (ТБ-030/032): одна запись <see cref="AuditAction.Search"/> на поиск; искомый текст — в чувствительной
/// части записи (он сам раскрывает, что искали), гриф записи — не ниже допуска субъекта. Поиски пакетов (по
/// расшифровкам одного дела) не вызываются — их порты используются напрямую, без второй записи.</para>
/// </remarks>
/// <param name="Text">Запрос: 2–200 знаков.</param>
/// <param name="Kinds">Какие виды искать; по умолчанию — все.</param>
/// <param name="Direction">Только дела отдела ОН/ОУ/ОТМ; <see langword="null"/> — все доступные.</param>
public sealed record UnifiedSearchQuery(string Text, SearchKinds Kinds = SearchKinds.All, CaseDirection? Direction = null)
    : IRequest<ResponseDto<UnifiedSearchResult>>, IAuditableRequest
{
    /// <summary>Предел строк в одном разделе выдачи.</summary>
    public const int Limit = 50;

    /// <summary>
    /// Пачка идентификаторов документов за одно обращение к порту документооборота — его предел партии
    /// (<c>DocumentLookup.MaxBatch</c>): сверх него идентификаторы порт молча игнорирует.
    /// </summary>
    public const int DocumentBatch = 200;

    /// <summary>Отказ: запрос короче двух знаков.</summary>
    public const string TooShortMessage = "Введите не меньше двух знаков.";

    /// <summary>Отказ: не выбран ни один вид выдачи.</summary>
    public const string NoKindsMessage = "Выберите, что искать.";

    // Область «все доступные дела»: страница заведомо больше любого реального числа дел одного субъекта.
    private const int ScopeSize = 10_000;

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    /// <remarks>Текст — обрезанный и не длиннее предела: запись журнала не раздувается мусорным вводом.</remarks>
    public string? AuditSummary =>
        $"investigation:search:kinds={Kinds & SearchKinds.All};direction={Direction?.ToString() ?? "-"};text={SearchTerms.Parse(Text).Raw}";

    /// <inheritdoc cref="UnifiedSearchQuery" />
    public sealed class Handler(
        ICaseStore cases,
        IPersonStore persons,
        IPersonRequisiteStore requisites,
        ICaseReportStore reports,
        ICaseDocumentStore caseDocuments,
        IDocumentLookup documents,
        IMediaCatalog media,
        IAccessContextProvider accessProvider) : IRequestHandler<UnifiedSearchQuery, ResponseDto<UnifiedSearchResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<UnifiedSearchResult>> Handle(UnifiedSearchQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var terms = SearchTerms.Parse(query.Text);
            if (!terms.IsSearchable)
            {
                return ResponseDto<UnifiedSearchResult>.BadRequest(TooShortMessage);
            }

            var kinds = query.Kinds & SearchKinds.All;
            if (kinds == SearchKinds.None)
            {
                return ResponseDto<UnifiedSearchResult>.BadRequest(NoKindsMessage);
            }

            // Fail-closed (ТБ-021): без контекста допуска GetCurrentAsync бросает — поиска нет.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // Область — доступные дела (полная решётка и роль, ТБ-071), при отделе — дела отдела.
            var scope = (await cases.ListAsync(
                new CaseFilter(PageSize: ScopeSize, Direction: query.Direction), access, cancellationToken)).Rows;
            var caseIds = scope.Select(c => c.Id).ToArray();
            var numbers = scope.ToDictionary(c => c.Id, c => c.Number);
            var truncated = SearchKinds.None;

            IReadOnlyList<T> Cap<T>(SearchKinds kind, IReadOnlyList<T> rows)
            {
                if (rows.Count <= Limit)
                {
                    return rows;
                }

                truncated |= kind;
                return rows.Take(Limit).ToList();
            }

            bool Wants(SearchKinds kind) => (kinds & kind) != 0 && caseIds.Length > 0;

            var foundCases = Wants(SearchKinds.Cases)
                ? Cap(SearchKinds.Cases, scope.Where(c => CaseMatches(c, terms)).Take(Limit + 1).ToList())
                : [];
            var foundPersons = Wants(SearchKinds.Persons)
                ? Cap(SearchKinds.Persons, await persons.SearchInCasesAsync(terms, caseIds, Limit + 1, access, cancellationToken))
                : [];
            var foundVehicles = Wants(SearchKinds.Vehicles)
                ? Cap(SearchKinds.Vehicles, await requisites.SearchVehiclesAsync(terms, caseIds, Limit + 1, access, cancellationToken))
                : [];
            var foundAddresses = Wants(SearchKinds.Addresses)
                ? Cap(SearchKinds.Addresses, await requisites.SearchAddressesAsync(terms, caseIds, Limit + 1, access, cancellationToken))
                : [];
            var foundReports = Wants(SearchKinds.Reports)
                ? Cap(SearchKinds.Reports, await reports.SearchAsync(terms, caseIds, Limit + 1, access, cancellationToken))
                : [];
            var foundTranscripts = Wants(SearchKinds.Transcripts)
                ? Cap(SearchKinds.Transcripts, await SearchTranscriptsAsync(terms, caseIds, access, cancellationToken))
                : [];
            var foundDocuments = Wants(SearchKinds.Documents)
                ? Cap(SearchKinds.Documents, await SearchDocumentsAsync(terms, caseIds, access, cancellationToken))
                : [];

            var result = new UnifiedSearchResult(
                terms.Raw, foundCases, foundPersons, foundVehicles, foundAddresses, foundReports, foundTranscripts, foundDocuments,
                numbers, truncated);
            return ResponseDto<UnifiedSearchResult>.Ok(result, result.TotalCount);
        }

        /// <summary>Дело подходит: номер, № задания или название содержат текст запроса (нормализованные одинаково).</summary>
        private static bool CaseMatches(CaseRow row, SearchTerms terms) =>
            RequisiteNormalizer.SearchText(row.Number).Contains(terms.Text, StringComparison.Ordinal)
            || RequisiteNormalizer.SearchText(row.TaskNumber).Contains(terms.Text, StringComparison.Ordinal)
            || RequisiteNormalizer.SearchText(row.Title).Contains(terms.Text, StringComparison.Ordinal);

        /// <summary>
        /// Расшифровки носителей дел области — портом каталога пакета «Медиа» (решётка по фрагменту и носителю на
        /// стороне БД), с делом носителя для перехода к месту записи.
        /// </summary>
        private async Task<IReadOnlyList<TranscriptSearchHit>> SearchTranscriptsAsync(
            SearchTerms terms, IReadOnlyCollection<int> caseIds, AccessContext access, CancellationToken cancellationToken)
        {
            var assetIds = await cases.ListMediaAssetIdsAsync(caseIds, cancellationToken);
            if (assetIds.Count == 0)
            {
                return [];
            }

            var hits = await media.SearchTranscriptsAsync(assetIds, terms.Raw, Limit + 1, access, cancellationToken);
            if (hits.Count == 0)
            {
                return [];
            }

            var owners = await cases.MapMediaAssetsAsync(hits.Select(h => h.AssetId).Distinct().ToList(), caseIds, cancellationToken);
            return hits
                .Where(h => owners.ContainsKey(h.AssetId))
                .Select(h => new TranscriptSearchHit(h.AssetId, owners[h.AssetId], h.AssetFileName, h.Kind, h.SegmentIndex, h.StartMs, h.Text))
                .ToList();
        }

        /// <summary>
        /// Документы дел области: привязки — из профиля (дела повторно под решёткой), карточки — портом
        /// документооборота пачками (документ вне допуска в ответе отсутствует, ТБ-021); совпадение — регистрационный
        /// номер, вид или краткое содержание. Пачки читаются, пока не набрано больше предела.
        /// </summary>
        private async Task<IReadOnlyList<DocumentSearchHit>> SearchDocumentsAsync(
            SearchTerms terms, IReadOnlyCollection<int> caseIds, AccessContext access, CancellationToken cancellationToken)
        {
            var links = await caseDocuments.ListLinksAsync(caseIds, access, cancellationToken);
            var owners = new Dictionary<int, int>();
            foreach (var link in links)
            {
                owners.TryAdd(link.DocumentId, link.CaseId);
            }

            var found = new List<DocumentSearchHit>();
            foreach (var batch in owners.Keys.Chunk(DocumentBatch))
            {
                var briefs = await documents.ResolveByIdsAsync(batch, access, cancellationToken);
                foreach (var id in batch)
                {
                    if (briefs.TryGetValue(id, out var brief) && DocumentMatches(brief, terms))
                    {
                        found.Add(new DocumentSearchHit(id, owners[id], brief.RegNumber, brief.RegDate, brief.TypeName, brief.ShortContent));
                    }
                }

                if (found.Count > Limit)
                {
                    break;
                }
            }

            return found;
        }

        private static bool DocumentMatches(DocumentBrief brief, SearchTerms terms) =>
            RequisiteNormalizer.SearchText(brief.RegNumber).Contains(terms.Text, StringComparison.Ordinal)
            || RequisiteNormalizer.SearchText(brief.TypeName).Contains(terms.Text, StringComparison.Ordinal)
            || RequisiteNormalizer.SearchText(brief.ShortContent).Contains(terms.Text, StringComparison.Ordinal);
    }
}

/// <summary>Проверка запроса единого поиска до обработчика: не короче двух знаков, хотя бы один вид.</summary>
public sealed class UnifiedSearchValidator : AbstractValidator<UnifiedSearchQuery>
{
    /// <summary>Правила.</summary>
    public UnifiedSearchValidator()
    {
        RuleFor(q => q.Text)
            .Must(text => SearchTerms.Parse(text).IsSearchable)
            .WithMessage(UnifiedSearchQuery.TooShortMessage);
        RuleFor(q => q.Kinds)
            .Must(kinds => (kinds & SearchKinds.All) != SearchKinds.None)
            .WithMessage(UnifiedSearchQuery.NoKindsMessage);
    }
}
