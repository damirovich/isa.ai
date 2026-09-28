using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация адресов фигурантов (<c>investigation.person_address</c>, ТФ-ПЕР-06).</summary>
public class PersonAddressConfiguration : IEntityTypeConfiguration<PersonAddress>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PersonAddress> builder)
    {
        builder.ToTable("person_address", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.Text).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.TextNormalized).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(2000);

        // Режимные поля — с фигуранта (ТБ-070), NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // FK внутри схемы: удаление фигуранта (каскадом от дела, ADR-0025) уносит его адреса.
        builder.HasOne(e => e.Person).WithMany()
               .HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.Cascade);

        // Пересечения по адресу (ТФ-ПЕР-07) — точное сравнение по нормализованному значению.
        builder.HasIndex(e => e.TextNormalized);
    }
}

/// <summary>Конфигурация автотранспорта фигурантов (<c>investigation.person_vehicle</c>, ТФ-ПЕР-06).</summary>
public class PersonVehicleConfiguration : IEntityTypeConfiguration<PersonVehicle>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<PersonVehicle> builder)
    {
        // Транспорт без госномера и без марки ничего не описывает: нужен хотя бы один из реквизитов.
        builder.ToTable("person_vehicle", InvestigationDbContext.Schema, table => table.HasCheckConstraint(
            "ck_person_vehicle_plate_or_make",
            "coalesce(btrim(plate_number), '') <> '' OR coalesce(btrim(make), '') <> ''"));
        builder.HasKey(e => e.Id);

        builder.Property(e => e.PlateNumber).HasMaxLength(20);
        builder.Property(e => e.PlateNormalized).HasMaxLength(20);
        builder.Property(e => e.Make).HasMaxLength(100);
        builder.Property(e => e.Model).HasMaxLength(100);
        builder.Property(e => e.Color).HasMaxLength(50);
        builder.Property(e => e.Notes).HasMaxLength(2000);

        // Режимные поля — с фигуранта (ТБ-070), NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // FK внутри схемы: удаление фигуранта уносит его транспорт.
        builder.HasOne(e => e.Person).WithMany()
               .HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.Cascade);

        // Пересечения по госномеру (ТФ-ПЕР-07) — главный случай макета заказчика.
        builder.HasIndex(e => e.PlateNormalized);
    }
}
