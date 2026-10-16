using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Ingestion;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.Corpus;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Storage;
using ISC.AI.Profile.Investigation.Application.Features.CaseChat;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Материалы чата в корпусе ядра (ADR-0051) на настоящей БД: расшифровка записи индексируется с метками времени, с
/// грифом носителя, видна только под решёткой, новая расшифровка заменяет прежнюю (старая уничтожается), уничтожение
/// носителя уносит текст; «Сведения дела» и сводки синхронизируются по отметкам — без изменений текст не перекладывается,
/// правка фигуранта даёт новую версию, снятая сводка уходит; уничтожение дела уносит всё; область чата собирает
/// документы, расшифровки, сводки и сведения дел. Эмбеддер — фиксированный фейк (проверяется не качество векторов).
/// </summary>
/// <remarks>Требуется Docker. Схемы <c>core</c>, <c>docflow</c>, <c>media</c>, <c>investigation</c> — в одном контейнере.</remarks>
[Trait("Category", "Gate")]
public sealed class ChatMaterialsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();
    private readonly string _files = Path.Combine(Path.GetTempPath(), "iscai-chat-materials-tests", Guid.NewGuid().ToString("N"));

    private CoreContextFactory _core = null!;
    private MediaContextFactory _media = null!;
    private InvestigationContextFactory _investigation = null!;
    private DocFlowContextFactory _docflow = null!;
    private IngestionPort _ingestion = null!;
    private DocumentPurger _purger = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var connection = _postgres.GetConnectionString();
        _core = new CoreContextFactory(connection);
        _media = new MediaContextFactory(connection);
        _investigation = new InvestigationContextFactory(connection);
        _docflow = new DocFlowContextFactory(connection);
        await using (var db = _core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = _media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = _docflow.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await InvestigationTestKit.MigrateAsync(_investigation);
        _ingestion = new IngestionPort(_core, new FixedEmbeddingGenerator(EmbeddingEntity.Dimensions), new SimpleTextChunker());
        _purger = new DocumentPurger(_core, new AuditWriter(_core));
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_files))
        {
            Directory.Delete(_files, recursive: true);
        }
    }

    [Fact(DisplayName = "ADR-0051: расшифровка — в корпус с метками времени и грифом носителя; только под решёткой; новая заменяет прежнюю; уничтожение носителя уносит текст")]
    public async Task Transcript_corpus_lifecycle()
    {
        var storage = new LocalFileStorage(Path.Combine(_files, "transcripts"));
        var store = new MediaStore(_media, storage);
        var asset = await store.ReceiveAsync(new MediaAssetDraft("звонок.ogg", "audio/ogg", MediaKind.Audio, 2, 7), new MemoryStream([1, 2, 3, 4]));
        await store.CompleteTranscriptionAsync(asset.AssetId,
            [new TranscriptSegmentDraft(0, 5_000, 7_000, "Алло, это я"), new TranscriptSegmentDraft(1, 65_000, 67_000, "Встречаемся у рынка")], "test-model");
        var corpus = new TranscriptCorpus(_media, new AllowAllAccessPolicy(), _ingestion, _purger, new TranscriptCorpusOptions());
        var owner = new AccessContext("1", 3, [7]);

        (await corpus.ListPendingAsync(10)).ShouldBe([asset.AssetId]);
        var first = await corpus.IndexAsync(asset.AssetId);
        first.Status.ShouldBe(TranscriptIndexStatus.Indexed);
        var firstDocument = first.CoreDocumentId.ShouldNotBeNull();
        (await corpus.ListPendingAsync(10)).ShouldBeEmpty();
        (await corpus.IndexAsync(asset.AssetId)).ShouldBe(new TranscriptIndexResult(TranscriptIndexStatus.Unchanged, firstDocument));

        await using (var db = _core.CreateDbContext())
        {
            var document = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == firstDocument);
            document.Classification.ShouldBe<short>(2);
            document.DivisionId.ShouldBe(7);
            document.Metadata.ShouldNotBeNull()[TranscriptCorpusText.AssetIdKey].ShouldBe(asset.AssetId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var text = string.Join("\n", await db.Chunks.AsNoTracking().Where(c => c.DocumentId == firstDocument).OrderBy(c => c.Ordinal).Select(c => c.Text).ToListAsync());
            text.ShouldContain("[00:00:05] Алло, это я");
            text.ShouldContain("[00:01:05] Встречаемся у рынка");
        }

        var scope = await corpus.ResolveAsync([asset.AssetId], owner);
        scope.Transcribed.ShouldBe(1);
        scope.Indexed.ShouldHaveSingleItem().ShouldBe(new TranscriptCorpusRef(asset.AssetId, firstDocument, "звонок.ogg", MediaKind.Audio, 2));
        var stranger = await corpus.ResolveAsync([asset.AssetId], new AccessContext("2", 3, [8]));
        stranger.Indexed.ShouldBeEmpty();
        stranger.Transcribed.ShouldBe(0);

        // Расшифровали заново: в ожидании, новая версия, прежний документ корпуса уничтожен (не «неактуален»).
        await store.CompleteTranscriptionAsync(asset.AssetId, [new TranscriptSegmentDraft(0, 1_000, 2_000, "Другая запись речи")], "test-model");
        (await corpus.ListPendingAsync(10)).ShouldBe([asset.AssetId]);
        var second = (await corpus.IndexAsync(asset.AssetId)).CoreDocumentId.ShouldNotBeNull();
        second.ShouldNotBe(firstDocument);
        await using (var db = _core.CreateDbContext())
        {
            (await db.Documents.AnyAsync(d => d.Id == firstDocument)).ShouldBeFalse();
        }

        (await new TranscriptCorpus(_media, new AllowAllAccessPolicy(), _ingestion, _purger, new TranscriptCorpusOptions(Enabled: false))
            .IndexAsync(asset.AssetId)).Status.ShouldBe(TranscriptIndexStatus.Disabled);

        // Уничтожение носителя (ТБ-064): документ корпуса — своим актом, затем строки носителя.
        (await new MediaPurger(_media, new AuditWriter(_core), storage, _purger).PurgeAsync(asset.AssetId, subjectId: 1)).Found.ShouldBeTrue();
        await using (var db = _core.CreateDbContext())
        {
            (await db.Documents.AnyAsync(d => d.Id == second)).ShouldBeFalse();
        }

        await using (var db = _media.CreateDbContext())
        {
            (await db.TranscriptIndexLinks.AnyAsync()).ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "ADR-0051: сведения дела и сводки — в корпус по отметкам: без изменений не перекладываются, правка фигуранта — новая версия, снятая сводка уходит, уничтожение дела уносит всё")]
    public async Task Case_corpus_sync_lifecycle()
    {
        await InvestigationTestKit.AssignRolesAsync(_investigation, (10, InvestigationRole.Administrator));
        var admin = InvestigationTestKit.Access(10, 3, 5);
        var cases = InvestigationTestKit.CreateCaseStore(_investigation, _core);
        var caseId = (await cases.CreateAsync(InvestigationTestKit.Draft("СВ-1", 5, 2, 10), admin)).CaseId;
        var personId = await AddPersonAsync(caseId, "Иванов Иван Иванович", "01KG123ABC");
        var reports = new CaseReportStore(_investigation, new InvestigationAccessPolicy(_investigation), new UserRoleStore(_core, _investigation),
            new CaseReportOptions(), TimeProvider.System);
        var (created, reportId) = await reports.CreateAsync(
            new CaseReportDraft(caseId, CaseReportKind.SummaryOn, new DateOnly(2026, 10, 12), personId,
                new CaseReportContent(Events: [new SummaryEvent("14:30", "ул. Ленина, 15", "вышел из дома", null, null)])),
            admin);
        created.ShouldBe(CaseReportWriteResult.Ok);
        var sync = new CaseCorpusSync(_investigation, _ingestion, _purger, TimeProvider.System);

        var refs = await sync.SyncAsync([caseId]);

        refs.Select(r => (r.Kind, r.SourceId, r.CaseId, r.Label)).OrderBy(r => r.Kind).ShouldBe(
        [
            (CaseCorpusKind.CaseCard, caseId, caseId, "Сведения дела № СВ-1"),
            (CaseCorpusKind.Report, reportId, caseId, "Сводка ОН от 12.10.2026 · дело № СВ-1"),
        ]);
        var card = refs.Single(r => r.Kind == CaseCorpusKind.CaseCard).CoreDocumentId;
        var summary = refs.Single(r => r.Kind == CaseCorpusKind.Report).CoreDocumentId;
        (await TextOfAsync(card)).ShouldContain("Иванов Иван Иванович");
        (await TextOfAsync(card)).ShouldContain("госномер 01KG123ABC");
        (await TextOfAsync(summary)).ShouldContain("Событие: 14:30 — ул. Ленина, 15; вышел из дома.");
        await using (var db = _core.CreateDbContext())
        {
            (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == card)).Classification.ShouldBe<short>(2);
        }

        // Без изменений — те же документы, новых нет.
        var documents = await CountCoreDocumentsAsync();
        (await sync.SyncAsync([caseId])).Select(r => r.CoreDocumentId).Order().ShouldBe(new[] { card, summary }.Order());
        (await CountCoreDocumentsAsync()).ShouldBe(documents);

        // Правка фигуранта — новая версия сведений; прежняя уничтожена.
        await using (var db = _investigation.CreateDbContext())
        {
            var person = await db.Persons.SingleAsync(p => p.Id == personId);
            person.Alias = "Ваня";
            await db.SaveChangesAsync();
        }

        var renewed = (await sync.SyncAsync([caseId])).Single(r => r.Kind == CaseCorpusKind.CaseCard).CoreDocumentId;
        renewed.ShouldNotBe(card);
        (await TextOfAsync(renewed)).ShouldContain("псевдоним «Ваня»");
        (await DocumentExistsAsync(card)).ShouldBeFalse();

        // Снятая с действия сводка — из корпуса уничтожением.
        (await reports.SetActiveAsync(reportId, false, admin)).ShouldBe(CaseReportWriteResult.Ok);
        (await sync.SyncAsync([caseId])).ShouldAllBe(r => r.Kind == CaseCorpusKind.CaseCard);
        (await DocumentExistsAsync(summary)).ShouldBeFalse();

        // Фоновое дозаполнение идёт партиями по возрастанию дела и заканчивается.
        (await sync.SyncNextAsync(0)).ShouldBe(caseId);
        (await sync.SyncNextAsync(caseId)).ShouldBeNull();

        // Уничтожение дела: документы корпуса — каждый своим актом.
        (await sync.PurgeCaseAsync(caseId, subjectId: 10)).ShouldBe(1);
        (await DocumentExistsAsync(renewed)).ShouldBeFalse();
        (await sync.ListAsync([caseId])).ShouldBeEmpty();
    }

    [Fact(DisplayName = "ADR-0051: область чата — документы дел (видимые и проиндексированные), расшифровки записей дел, сводки и сведения; готовность по видам")]
    public async Task Area_materials_are_collected()
    {
        await InvestigationTestKit.AssignRolesAsync(_investigation, (20, InvestigationRole.Administrator));
        var admin = InvestigationTestKit.Access(20, 3, 6);
        var cases = InvestigationTestKit.CreateCaseStore(_investigation, _core);
        var scope = InvestigationTestKit.CreateCaseScope(_investigation, _core);
        var caseId = (await cases.CreateAsync(InvestigationTestKit.Draft("ОБЛ-1", 6, 1, 20), admin)).CaseId;
        await AddPersonAsync(caseId, "Петров Пётр", null);

        var storage = new LocalFileStorage(Path.Combine(_files, "area"));
        var store = new MediaStore(_media, storage);
        var asset = await store.ReceiveAsync(new MediaAssetDraft("запись.ogg", "audio/ogg", MediaKind.Audio, 1, 6), new MemoryStream([9, 8, 7, 6]));
        await scope.LinkAssetAsync(caseId, asset.AssetId, null, 20);
        await store.CompleteTranscriptionAsync(asset.AssetId, [new TranscriptSegmentDraft(0, 3_000, 4_000, "Деньги передам завтра")], "test-model");
        var transcripts = new TranscriptCorpus(_media, new AllowAllAccessPolicy(), _ingestion, _purger, new TranscriptCorpusOptions());
        var transcriptDocument = (await transcripts.IndexAsync(asset.AssetId)).CoreDocumentId.ShouldNotBeNull();

        var scopes = new CaseChatScopes(_investigation, new InvestigationAccessPolicy(_investigation), new UserRoleStore(_core, _investigation));
        var area = (await CaseChatArea.ResolveAsync(scopes, caseId, admin, CancellationToken.None)).ShouldNotBeNull();
        var resolver = new ChatMaterialResolver(
            new CaseDocumentStore(_investigation, new InvestigationAccessPolicy(_investigation), new UserRoleStore(_core, _investigation)),
            new DocumentLookup(_docflow, new InvestigationAccessPolicy(_investigation)),
            cases,
            transcripts,
            new CaseCorpusSync(_investigation, _ingestion, _purger, TimeProvider.System));

        var before = await resolver.ResolveAsync(area, admin, sync: false, CancellationToken.None);
        before.Coverage.ShouldBe(new ChatCoverage(1, 0, 0, TranscriptsReady: 1, TranscriptsIndexed: 1, Reports: 0, CaseCards: 0));

        var materials = await resolver.ResolveAsync(area, admin, sync: true, CancellationToken.None);

        materials.Items.Select(i => (i.Kind, i.TargetId, i.CaseId)).ShouldBe(
        [
            (ChatSourceKind.Transcript, asset.AssetId, caseId),
            (ChatSourceKind.CaseCard, caseId, caseId),
        ]);
        materials.Items[0].CoreDocumentId.ShouldBe(transcriptDocument);
        materials.Items[0].Label.ShouldBe($"Расшифровка «запись.ogg» · носитель № {asset.AssetId} · дело № ОБЛ-1");
        materials.Coverage.CaseCards.ShouldBe(1);

        // Порт профиля для пакета «Чат» (ADR-0052) на тех же настоящих хранилищах: область дела, её материалы и подпись
        // найденного фрагмента расшифровки — с моментом записи и ссылкой на него; ключ уничтожения — дело.
        var profile = new InvestigationChatProfile(scopes, resolver, new UserRoleStore(_core, _investigation), new FixedSubjectProvider(20));
        var key = CaseChatAreas.KeyOf(caseId);
        var chatArea = (await profile.ResolveAreaAsync(key, admin)).ShouldNotBeNull();
        chatArea.ScopeKeys.ShouldBe([key]);
        (await profile.ResolveAreaAsync(CaseChatAreas.KeyOf(caseId + 1000), admin)).ShouldBeNull();
        (await profile.CanUseAsync()).ShouldBeTrue();

        var chatScope = await profile.ResolveScopeAsync(chatArea, admin, forQuestion: false);
        chatScope.Unavailable.ShouldBeNull();
        chatScope.DocumentIds.ShouldNotBeNull().ShouldContain(transcriptDocument);
        var source = chatScope.Describe(new ISC.AI.Abstractions.Retrieval.RetrievedChunk(1, transcriptDocument, "[00:00:03] Деньги передам завтра", 1, 6, true, 0.1))
            .ShouldNotBeNull();
        source.Href.ShouldBe($"/media/assets/{asset.AssetId}?t=3000");
        source.Label.ShouldEndWith("· [00:00:03]");
        source.ScopeKey.ShouldBe(key);
    }

    // ---------- обвязка ----------

    private async Task<int> AddPersonAsync(int caseId, string name, string? plate)
    {
        await using var db = _investigation.CreateDbContext();
        var person = new Person { CaseId = caseId, DisplayName = name, Role = PersonRole.Target, Classification = 1, DivisionId = 5 };
        db.Persons.Add(person);
        await db.SaveChangesAsync();
        if (plate is not null)
        {
            db.PersonVehicles.Add(new PersonVehicle { PersonId = person.Id, Make = "Toyota", Model = "Camry", PlateNumber = plate, Classification = 1, DivisionId = 5 });
            await db.SaveChangesAsync();
        }

        return person.Id;
    }

    private async Task<string> TextOfAsync(int documentId)
    {
        await using var db = _core.CreateDbContext();
        return string.Join("\n", await db.Chunks.AsNoTracking().Where(c => c.DocumentId == documentId).OrderBy(c => c.Ordinal).Select(c => c.Text).ToListAsync());
    }

    private async Task<bool> DocumentExistsAsync(int documentId)
    {
        await using var db = _core.CreateDbContext();
        return await db.Documents.AnyAsync(d => d.Id == documentId);
    }

    private async Task<int> CountCoreDocumentsAsync()
    {
        await using var db = _core.CreateDbContext();
        return await db.Documents.CountAsync();
    }
}
