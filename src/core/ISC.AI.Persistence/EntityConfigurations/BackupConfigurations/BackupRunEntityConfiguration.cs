using ISC.AI.Abstractions.Backup;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Persistence.EntityConfigurations.BackupConfigurations;

/// <summary>Конфигурация журнала резервных копий (<c>core.backup_run</c>, ADR-0063).</summary>
public class BackupRunEntityConfiguration : IEntityTypeConfiguration<BackupRunEntity>
{
    public void Configure(EntityTypeBuilder<BackupRunEntity> builder)
    {
        builder.ToTable("backup_run", CoreDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Status).HasConversion<short>();
        builder.Property(e => e.VerifyStatus).HasConversion<short?>();
        builder.Property(e => e.FolderName).HasMaxLength(BackupRunEntity.MaxFolderLength);
        builder.Property(e => e.Error).HasMaxLength(BackupRunEntity.MaxNoteLength);
        builder.Property(e => e.VerifyNote).HasMaxLength(BackupRunEntity.MaxNoteLength);

        // ИНВАРИАНТ (ADR-0063): копия пишется не больше одной за раз — второй прогон «идёт» база не примет даже при двух
        // одновременных нажатиях (частичный уникальный индекс по состоянию «идёт»).
        builder.HasIndex(e => e.Status)
            .IsUnique()
            .HasFilter($"status = {(short)BackupRunStatus.Running}")
            .HasDatabaseName("ux_backup_run_single_running");

        builder.HasIndex(e => e.StartedAt);
    }
}
