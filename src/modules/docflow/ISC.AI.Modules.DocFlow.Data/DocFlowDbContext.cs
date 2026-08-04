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
/// СКЕЛЕТ (этап 0 задачи Э4-35): сущности переносятся из СКИД на этапе 2, поэтому контекст пока пуст.
/// </remarks>
public class DocFlowDbContext(DbContextOptions<DocFlowDbContext> options) : AuditedDbContext(options)
{
    /// <summary>Имя схемы модуля документооборота.</summary>
    public const string Schema = "docflow";

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
