using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Хранилище справочника типов документов поверх <see cref="DocFlowDbContext"/> (ТЗ СКИД §3.1).
/// Контекст — на операцию, через фабрику (ТС-008, Blazor Server).
/// </summary>
public sealed class DocumentTypeStore(IDbContextFactory<DocFlowDbContext> contextFactory) : IDocumentTypeStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentTypeItem>> ListAsync(
        DocumentGroup? group = null, bool? isActive = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.DocumentTypes.AsNoTracking();
        if (group is { } g)
        {
            query = query.Where(t => t.Group == g);
        }

        if (isActive is { } active)
        {
            query = query.Where(t => t.IsActive == active);
        }

        return await query
            .OrderBy(t => t.Name)
            // CanDelete считается подзапросом В ТОМ ЖЕ запросе: иначе экран делал бы по обращению
            // к БД на каждую строку справочника.
            .Select(t => new DocumentTypeItem(
                t.Id, t.Name, t.Group, t.IsActive,
                !db.Documents.Any(d => d.TypeId == t.Id)))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int?> CreateAsync(
        string name, DocumentGroup group, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Дружелюбный отказ до вставки; гонку двух операторов добивает unique-индекс БД.
        if (await db.DocumentTypes.AnyAsync(t => t.Name == name, cancellationToken))
        {
            return null;
        }

        var entity = new DocumentType { Name = name, Group = group, IsActive = isActive };
        db.DocumentTypes.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    /// <inheritdoc />
    public async Task<DocumentTypeWriteResult> UpdateAsync(
        int id, string name, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (entity is null)
        {
            return DocumentTypeWriteResult.NotFound;
        }

        if (await db.DocumentTypes.AnyAsync(t => t.Name == name && t.Id != id, cancellationToken))
        {
            return DocumentTypeWriteResult.NameTaken;
        }

        entity.Name = name;
        entity.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
        return DocumentTypeWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<DocumentTypeWriteResult> ChangeGroupAsync(
        int id, DocumentGroup newGroup, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (entity is null)
        {
            return DocumentTypeWriteResult.NotFound;
        }

        // ИНВАРИАНТ (ТЗ СКИД §3.1): смена группы запрещена при наличии документов типа — иначе у
        // зарегистрированных документов «задним числом» поменялось бы поведение (назначения/статусы).
        if (await db.Documents.AnyAsync(d => d.TypeId == id, cancellationToken))
        {
            return DocumentTypeWriteResult.HasDocuments;
        }

        entity.Group = newGroup;
        await db.SaveChangesAsync(cancellationToken);
        return DocumentTypeWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<DocumentTypeWriteResult> DeleteAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (entity is null)
        {
            return DocumentTypeWriteResult.NotFound;
        }

        // Тот же инвариант, что у смены группы: использованный тип не удаляется — у документов
        // пропала бы группа, а с ней и правила их поведения (§3.1). Проверка ЗДЕСЬ, а не только
        // в форме: признак CanDelete в списке мог устареть, пока экран был открыт.
        if (await db.Documents.AnyAsync(d => d.TypeId == id, cancellationToken))
        {
            return DocumentTypeWriteResult.HasDocuments;
        }

        db.DocumentTypes.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return DocumentTypeWriteResult.Ok;
    }
}
