using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Классификатор видов нарушений (<see cref="IViolationCategoryStore"/>, Приложение §4):
/// два уровня «сфера → вид», схема <c>inspector</c>.
/// </summary>
public sealed class ViolationCategoryStore(IDbContextFactory<InspectorDbContext> contextFactory)
    : IViolationCategoryStore
{
    // Стартовый набор сфер — из прототипа (Э5-01); виды внутри сфер заводит администратор
    // по своей практике: придумывать их за инспекцию было бы фальшивой конкретикой.
    private static readonly string[] DefaultSpheres =
    [
        "Агентурная работа",
        "Аналитическая обработка данных",
        "Документооборот",
        "Планирование",
        "Исполнение поручений",
        "Взаимодействие подразделений",
        "Работа по направлениям",
    ];

    /// <inheritdoc />
    public async Task<IReadOnlyList<ViolationCategoryNode>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ViolationCategories.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.ParentId,
                ViolationCount = db.Violations.Count(v => v.CategoryId == c.Id),
            })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(r => new ViolationCategoryNode(r.Id, r.Name, r.ParentId, r.ViolationCount))];
    }

    /// <inheritdoc />
    public async Task<(ViolationWriteResult Result, int CategoryId)> CreateAsync(
        string name, int? parentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (parentId is { } parent)
        {
            var parentLevel = await db.ViolationCategories.AsNoTracking()
                .Where(c => c.Id == parent)
                .Select(c => new { c.ParentId })
                .FirstOrDefaultAsync(cancellationToken);
            if (parentLevel is null)
            {
                return (ViolationWriteResult.NotFound, 0);
            }

            // Классификатор двухуровневый по ТЗ: вид нельзя вкладывать в вид.
            if (parentLevel.ParentId is not null)
            {
                return (ViolationWriteResult.CategoryNotLeaf, 0);
            }
        }

        var normalized = name.Trim();
        if (await db.ViolationCategories.AnyAsync(
                c => c.ParentId == parentId && c.Name == normalized, cancellationToken))
        {
            return (ViolationWriteResult.DuplicateName, 0);
        }

        var category = new ViolationCategory { Name = normalized, ParentId = parentId };
        db.ViolationCategories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return (ViolationWriteResult.Ok, category.Id);
    }

    /// <inheritdoc />
    public async Task<ViolationWriteResult> RenameAsync(
        int categoryId, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var category = await db.ViolationCategories.FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return ViolationWriteResult.NotFound;
        }

        var normalized = name.Trim();
        if (await db.ViolationCategories.AnyAsync(
                c => c.Id != categoryId && c.ParentId == category.ParentId && c.Name == normalized, cancellationToken))
        {
            return ViolationWriteResult.DuplicateName;
        }

        category.Name = normalized;
        await db.SaveChangesAsync(cancellationToken);
        return ViolationWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<ViolationWriteResult> DeleteAsync(int categoryId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var category = await db.ViolationCategories.FirstOrDefaultAsync(c => c.Id == categoryId, cancellationToken);
        if (category is null)
        {
            return ViolationWriteResult.NotFound;
        }

        // За видом числятся нарушения или за сферой — виды: удалять нельзя, статистика осиротела бы.
        var inUse = await db.Violations.AnyAsync(v => v.CategoryId == categoryId, cancellationToken)
            || await db.ViolationCategories.AnyAsync(c => c.ParentId == categoryId, cancellationToken);
        if (inUse)
        {
            return ViolationWriteResult.InUse;
        }

        db.ViolationCategories.Remove(category);
        await db.SaveChangesAsync(cancellationToken);
        return ViolationWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<int> SeedDefaultsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Идемпотентно: только на ПУСТОМ классификаторе — начатое администратором не дополняем,
        // чтобы не навязывать сферы, от которых он, возможно, отказался.
        if (await db.ViolationCategories.AnyAsync(cancellationToken))
        {
            return 0;
        }

        foreach (var sphere in DefaultSpheres)
        {
            db.ViolationCategories.Add(new ViolationCategory { Name = sphere, ParentId = null });
        }

        await db.SaveChangesAsync(cancellationToken);
        return DefaultSpheres.Length;
    }
}
