using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация событий — «дел-посредников» связи объектов (<c>investigation.link_event</c>, ТФ-ПЕР-11, ADR-0056).</summary>
public class LinkEventConfiguration : IEntityTypeConfiguration<LinkEvent>
{
    /// <summary>Наибольшая длина названия события.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Наибольшая длина описания события.</summary>
    public const int MaxDescriptionLength = 4000;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LinkEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Событие без названия ничего не описывает; вид — только из перечня (ADR-0056 п. 1).
        builder.ToTable("link_event", InvestigationDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_link_event_title", "btrim(title) <> ''");
            table.HasCheckConstraint("ck_link_event_kind", "kind BETWEEN 1 AND 4");
        });
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(MaxTitleLength).IsRequired();
        builder.Property(e => e.Description).HasMaxLength(MaxDescriptionLength);

        // Режимные поля (ТБ-020): гриф — не ниже грифов участников, подразделение — дела, откуда заведено. NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        builder.HasMany(e => e.Participants).WithOne(p => p.Event)
               .HasForeignKey(p => p.EventId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Конфигурация участников событий (<c>investigation.link_event_participant</c>, ТФ-ПЕР-11, ADR-0056).</summary>
public class LinkEventParticipantConfiguration : IEntityTypeConfiguration<LinkEventParticipant>
{
    /// <summary>Наибольшая длина роли в событии.</summary>
    public const int MaxRoleLength = 200;

    /// <summary>Наибольшая длина госномера транспорта контакта (как у транспорта фигуранта).</summary>
    public const int MaxPlateLength = 20;

    /// <summary>Наибольшая длина марки и модели транспорта контакта.</summary>
    public const int MaxVehicleDetailsLength = 200;

    /// <summary>Наибольшая длина адреса контакта (как у адреса фигуранта).</summary>
    public const int MaxAddressLength = 1000;

    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<LinkEventParticipant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Уровень связи — только «верно / предположительно / неверно» [АИС ПМИ 3].
        builder.ToTable("link_event_participant", InvestigationDbContext.Schema, table =>
            table.HasCheckConstraint("ck_link_event_participant_level", "level BETWEEN 1 AND 3"));
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Role).HasMaxLength(MaxRoleLength);
        builder.Property(e => e.Level).IsRequired();
        builder.Property(e => e.InSearch).IsRequired();
        builder.Property(e => e.VehiclePlate).HasMaxLength(MaxPlateLength);
        builder.Property(e => e.VehiclePlateNormalized).HasMaxLength(MaxPlateLength);
        builder.Property(e => e.VehicleDetails).HasMaxLength(MaxVehicleDetailsLength);
        builder.Property(e => e.ContactAddress).HasMaxLength(MaxAddressLength);
        builder.Property(e => e.ContactAddressNormalized).HasMaxLength(MaxAddressLength);

        // Режимные поля — с фигуранта (ТБ-070), NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // Удаление фигуранта (в том числе уничтожением дела, ADR-0025) уносит его участие.
        builder.HasOne(e => e.Person).WithMany()
               .HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.Cascade);

        // Фигурант участвует в событии один раз; события фигуранта и пересечения по местам посещения — по индексам.
        builder.HasIndex(e => new { e.EventId, e.PersonId }).IsUnique();
        builder.HasIndex(e => e.PersonId);
        builder.HasIndex(e => e.ContactAddressNormalized);
        builder.HasIndex(e => e.VehiclePlateNormalized);
    }
}
