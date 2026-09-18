using ISC.AI.Modules.Media.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.Media.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы поисковых сессий (<c>media.search_session</c>, ТО-инф-12).</summary>
public sealed class SearchSessionConfiguration : IEntityTypeConfiguration<SearchSession>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SearchSession> builder)
    {
        builder.ToTable("search_session", MediaDbContext.Schema);
        builder.HasKey(s => s.Id);

        builder.Property(s => s.AuthorizationRef).HasMaxLength(200).IsRequired();
        builder.Property(s => s.ProbeSha256).HasMaxLength(64).IsRequired();
        builder.Property(s => s.ProbeCropStoredFileName).HasMaxLength(64);
        builder.Property(s => s.DetectorVersion).HasMaxLength(50).IsRequired();
        builder.Property(s => s.EmbedderVersion).HasMaxLength(50).IsRequired();

        // Область поиска — массив идентификаторов дел (integer[] Postgres, нативно через Npgsql):
        // дела — значения из схемы профиля, FK через границу схем нет (ТО-инф-08).
        builder.Property(s => s.CaseIds).HasColumnType("integer[]").IsRequired();

        // Режимные поля обязательны (ТБ-070/024): сессия без грифа дела в схему не попадает.
        builder.Property(s => s.Classification).IsRequired();
        builder.Property(s => s.DivisionId).IsRequired();

        builder.HasIndex(s => s.CaseId);
        builder.HasIndex(s => new { s.Classification, s.DivisionId });
        // Вырезка пробы разрешается в файл по (сессия, имя) — имя уникально в пределах категории media-probes.
        // Уникальность — только для пробы-изображения (probe_face_id IS NULL): у пробы-лица носителя своей вырезки
        // нет, и повторный поиск по одному лицу не должен упираться в индекс (ТФ-ПЛ-03, страховка на уровне БД).
        builder.HasIndex(s => s.ProbeCropStoredFileName).IsUnique()
            .HasFilter("probe_crop_stored_file_name IS NOT NULL AND probe_face_id IS NULL");
    }
}
