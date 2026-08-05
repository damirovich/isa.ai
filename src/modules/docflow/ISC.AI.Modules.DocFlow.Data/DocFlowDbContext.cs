using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Контекст данных модуля документооборота (схема <c>docflow</c>): документы, поручения, сроки,
/// исполнители, комментарии. Ведёт собственную историю миграций
/// (<c>docflow.__ef_migrations_history</c>, ADR-0003, ADR-0017, ТО-инф-01).
/// </summary>
/// <remarks>
/// Наследует общий механизм аудита/таймстемпов/soft-delete у ядра (<see cref="AuditedDbContext"/>),
/// чтобы аудит не расходился между контекстами. FK создаются только ВНУТРИ схемы <c>docflow</c>;
/// связь с корпусом ядра (<c>core.document</c>) — СЛАБАЯ по значению идентификатора, без FK через
/// границу схем (ТО-инф-06), по образцу <c>inspector.norm_document_link</c>. Под Blazor Server
/// создаётся через <c>IDbContextFactory</c> (ТС-008).
///
/// Перенесён справочник типов (этап 1 Э4-35); остальные сущности СКИД — этап 2.
/// </remarks>
public class DocFlowDbContext(DbContextOptions<DocFlowDbContext> options) : AuditedDbContext(options)
{
    /// <summary>Имя схемы модуля документооборота.</summary>
    public const string Schema = "docflow";

    /// <summary>Справочник типов документов (ТЗ СКИД §3.1).</summary>
    public DbSet<DocumentType> DocumentTypes { get; set; } = null!;

    /// <summary>Документы (ТЗ СКИД §3.2). НЕ путать с <c>core.document</c> — индексом корпуса.</summary>
    public DbSet<Document> Documents { get; set; } = null!;

    /// <summary>Версионируемые файлы документов (§3.3).</summary>
    public DbSet<DocumentFile> DocumentFiles { get; set; } = null!;

    /// <summary>Сопутствующие файлы документов.</summary>
    public DbSet<DocumentAttachment> DocumentAttachments { get; set; } = null!;

    /// <summary>Назначения по подразделениям (§4.1).</summary>
    public DbSet<DocumentAssignment> DocumentAssignments { get; set; } = null!;

    /// <summary>История переходов статусов назначений (§4.8).</summary>
    public DbSet<AssignmentStatusHistory> AssignmentStatusHistories { get; set; } = null!;

    /// <summary>Файлы к переходам статусов (§4.2).</summary>
    public DbSet<StatusHistoryFile> StatusHistoryFiles { get; set; } = null!;

    /// <summary>Продления сроков назначений (§4.6).</summary>
    public DbSet<DeadlineExtension> DeadlineExtensions { get; set; } = null!;

    /// <summary>Файлы-обоснования продлений (§4.6).</summary>
    public DbSet<DeadlineExtensionFile> DeadlineExtensionFiles { get; set; } = null!;

    /// <summary>Мостики «документ ↔ корпус ядра» (этап 7 Э4-35, ADR-0017 п.6).</summary>
    public DbSet<DocumentIndexLink> DocumentIndexLinks { get; set; } = null!;

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(DocFlowDbContext).Assembly,
            type => type.Namespace?.Contains("EntityConfigurations", StringComparison.Ordinal) == true);

        ApplySoftDeleteQueryFilters(modelBuilder);
    }
}
