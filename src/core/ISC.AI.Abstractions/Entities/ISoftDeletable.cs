namespace ISC.AI.Abstractions.Entities;

/// <summary>Описывает сущность, поддерживающую обратимое (мягкое) удаление.</summary>
public interface ISoftDeletable
{
    /// <summary>Удалена ли сущность.</summary>
    bool IsDeleted { get; set; }
}
