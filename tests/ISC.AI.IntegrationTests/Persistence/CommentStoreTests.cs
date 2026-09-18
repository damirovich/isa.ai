using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Комментарии к документу (ТЗ СКИД §4.8, этап 2.2 Э4-35) на реальном PostgreSQL: ответы, упоминания
/// с двойной проверкой, мягкое удаление с обнулением содержимого НА СЕРВЕРЕ, закрытие обсуждения
/// только для корневого, правка только автором. Требуется Docker.
/// </summary>
public sealed class CommentStoreTests : IAsyncLifetime
{
    // Допуск автора документов теста: предмет проверки — комментарии, не разграничение.
    private static readonly AccessContext FullAccess = new("42", 10, [5]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Комментарии §4.8: ответ, упоминания (двойная проверка), правка автором, удаление, закрытие обсуждения")]
    public async Task Comment_lifecycle_end_to_end()
    {
        var docFlowFactory = new DocFlowContextFactory(_postgres.GetConnectionString());
        var coreFactory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = docFlowFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        int authorId, mentionedId, inactiveId;
        await using (var core = coreFactory.CreateDbContext())
        {
            await core.Database.MigrateAsync();
            var author = new AppUserEntity { UserName = "author", DisplayName = "Автор А.А." };
            var mentioned = new AppUserEntity { UserName = "mentioned", DisplayName = "Упомянутый У.У." };
            var inactive = new AppUserEntity { UserName = "inactive", DisplayName = "Уволенный", IsActive = false };
            core.Users.AddRange(author, mentioned, inactive);
            await core.SaveChangesAsync();
            (authorId, mentionedId, inactiveId) = (author.Id, mentioned.Id, inactive.Id);
        }

        var typeStore = new DocumentTypeStore(docFlowFactory);
        var documents = new DocumentStore(docFlowFactory, new TempFileStorage(), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);
        var comments = new CommentStore(docFlowFactory, coreFactory, new TempFileStorage());

        var typeId = await typeStore.CreateAsync("Справка", DocumentGroup.Storage, isActive: true);
        var doc = await documents.CreateAsync(
            new DocumentDraft("К-1", new DateOnly(2026, 8, 6), typeId!.Value, DocumentDirection.Internal,
                null, "Документ с обсуждением", null, null, null, null, 0, 5, authorId),
            [], useCommonDeadline: false, commonDeadline: null, FullAccess);
        doc.Status.ShouldBe(DocumentWriteStatus.Ok);

        // Корневой комментарий: упомянуты активный, НЕАКТИВНЫЙ и «заявленный, но не написанный в тексте».
        // Сохраниться должен ТОЛЬКО активный, реально упомянутый в тексте (пересечение трёх источников).
        var content = $"Прошу проверить @[Упомянутый](user:{mentionedId}) и @[Уволенный](user:{inactiveId})";
        var (rootStatus, rootId) = await comments.AddAsync(
            new CommentDraft(doc.DocumentId, null, content, CommentType.Question,
                [mentionedId, inactiveId, 999_999], Files: null),
            authorId);
        rootStatus.ShouldBe(CommentWriteStatus.Ok);

        var feed = await comments.ListAsync(doc.DocumentId);
        var root = feed.ShouldHaveSingleItem();
        root.Mentions.Select(m => m.UserId).ShouldBe([mentionedId]);
        root.AuthorName.ShouldBe("Автор А.А.");
        root.CommentType.ShouldBe(CommentType.Question);

        // Ответ на корневой.
        var (replyStatus, replyId) = await comments.AddAsync(
            new CommentDraft(doc.DocumentId, rootId, "Принято в работу", CommentType.Approval, [], null), mentionedId);
        replyStatus.ShouldBe(CommentWriteStatus.Ok);
        (await comments.ListAsync(doc.DocumentId)).Count.ShouldBe(2);

        // Ответ на комментарий ЧУЖОГО документа — отказ (защита от подмены родителя).
        var otherDoc = await documents.CreateAsync(
            new DocumentDraft("К-2", new DateOnly(2026, 8, 6), typeId.Value, DocumentDirection.Internal,
                null, "Другой документ", null, null, null, null, 0, 5, authorId),
            [], useCommonDeadline: false, commonDeadline: null, FullAccess);
        (await comments.AddAsync(
            new CommentDraft(otherDoc.DocumentId, rootId, "чужая ветка", CommentType.Remark, [], null), authorId))
            .Status.ShouldBe(CommentWriteStatus.ParentMismatch);

        // Правка: только автором; чужая — отказ, текст не меняется.
        (await comments.UpdateAsync(rootId, "изменено чужим", [], mentionedId)).ShouldBe(CommentWriteStatus.NotAuthor);
        (await comments.UpdateAsync(rootId, "Исправленный текст", [], authorId)).ShouldBe(CommentWriteStatus.Ok);
        var afterEdit = (await comments.ListAsync(doc.DocumentId)).Single(c => c.Id == rootId);
        afterEdit.Content.ShouldBe("Исправленный текст");
        afterEdit.IsEdited.ShouldBeTrue();
        afterEdit.Mentions.ShouldBeEmpty(); // упоминания пересобраны: в новом тексте токенов нет

        // Закрытие обсуждения: только КОРНЕВОЕ; на ответе — отказ. Повтор идемпотентен.
        (await comments.SetResolvedAsync(replyId, true, authorId)).ShouldBe(CommentWriteStatus.OnlyRootCanBeResolved);
        (await comments.SetResolvedAsync(rootId, true, mentionedId)).ShouldBe(CommentWriteStatus.Ok);
        (await comments.SetResolvedAsync(rootId, true, mentionedId)).ShouldBe(CommentWriteStatus.Ok);
        var resolved = (await comments.ListAsync(doc.DocumentId)).Single(c => c.Id == rootId);
        resolved.IsResolved.ShouldBeTrue();
        resolved.ResolvedByUserId.ShouldBe(mentionedId);

        // Мягкое удаление: содержимое обнуляется НА СЕРВЕРЕ, ответ в ветке остаётся читаемым.
        (await comments.DeleteAsync(rootId, mentionedId)).ShouldBe(CommentWriteStatus.NotAuthor);
        (await comments.DeleteAsync(rootId, authorId)).ShouldBe(CommentWriteStatus.Ok);
        (await comments.DeleteAsync(rootId, authorId)).ShouldBe(CommentWriteStatus.Ok); // идемпотентно

        var afterDelete = await comments.ListAsync(doc.DocumentId);
        var deletedRoot = afterDelete.Single(c => c.Id == rootId);
        deletedRoot.IsDeleted.ShouldBeTrue();
        deletedRoot.Content.ShouldBeEmpty();
        deletedRoot.Mentions.ShouldBeEmpty();
        deletedRoot.Files.ShouldBeEmpty();
        afterDelete.Single(c => c.Id == replyId).Content.ShouldBe("Принято в работу");

        // Правка удалённого — отказ.
        (await comments.UpdateAsync(rootId, "воскрешение", [], authorId)).ShouldBe(CommentWriteStatus.AlreadyDeleted);
    }
}
