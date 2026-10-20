using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// ADR-0055: гражданство и ориентировка в анкете фигуранта, номера телефонов (<c>person_phone</c>) и стартовый список
    /// справочника «Страны» (вид 6) — подсказки гражданства. Страны вставляются, только если такой записи ещё нет:
    /// правки Администратора не затираются; полное наполнение справочников — ЭС3в-09.
    /// </summary>
    public partial class QuestionnaireAndPhones : Migration
    {
        // Вид справочника «Страны» (ReferenceKind.Country).
        private const int CountryKind = 6;

        // Кыргызстан — первым, затем соседи и частые страны пребывания; остальные Администратор добавит сам.
        private static readonly (string Name, int SortOrder)[] StarterCountries =
        [
            ("Кыргызстан", 1),
            ("Казахстан", 10), ("Узбекистан", 10), ("Таджикистан", 10), ("Туркменистан", 10), ("Китай", 10),
            ("Россия", 20), ("Беларусь", 20), ("Украина", 20), ("Азербайджан", 20), ("Армения", 20), ("Грузия", 20),
            ("Афганистан", 30), ("Пакистан", 30), ("Иран", 30), ("Индия", 30), ("Монголия", 30), ("Турция", 30),
            ("Объединённые Арабские Эмираты", 30), ("Республика Корея", 30), ("Германия", 40), ("США", 40),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "citizenship",
                schema: "investigation",
                table: "person",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "orientation",
                schema: "investigation",
                table: "person",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "person_phone",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    person_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    number_normalized = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_person_phone", x => x.id);
                    table.CheckConstraint("ck_person_phone_normalized_digits", "number_normalized <> '' AND number_normalized ~ '^[0-9]+$'");
                    table.ForeignKey(
                        name: "fk_person_phone_person_person_id",
                        column: x => x.person_id,
                        principalSchema: "investigation",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_person_phone_number_normalized",
                schema: "investigation",
                table: "person_phone",
                column: "number_normalized");

            migrationBuilder.CreateIndex(
                name: "ix_person_phone_person_id",
                schema: "investigation",
                table: "person_phone",
                column: "person_id");

            foreach (var (name, sortOrder) in StarterCountries)
            {
                migrationBuilder.Sql(
                    "INSERT INTO investigation.reference_item (kind, name, code, sort_order, is_active, created_at) "
                    + $"SELECT {CountryKind}, '{name}', NULL, {sortOrder}, TRUE, now() "
                    + $"WHERE NOT EXISTS (SELECT 1 FROM investigation.reference_item WHERE kind = {CountryKind} AND lower(name) = lower('{name}'));");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Вида «Страны» до этой миграции не было: на его записи ничего не ссылается (гражданство — текст).
            migrationBuilder.Sql($"DELETE FROM investigation.reference_item WHERE kind = {CountryKind};");

            migrationBuilder.DropTable(
                name: "person_phone",
                schema: "investigation");

            migrationBuilder.DropColumn(
                name: "citizenship",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "orientation",
                schema: "investigation",
                table: "person");
        }
    }
}
