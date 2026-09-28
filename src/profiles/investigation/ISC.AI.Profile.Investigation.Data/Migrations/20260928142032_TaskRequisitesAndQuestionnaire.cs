using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskRequisitesAndQuestionnaire : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "alias",
                schema: "investigation",
                table: "person",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "birth_date",
                schema: "investigation",
                table: "person",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "birth_place",
                schema: "investigation",
                table: "person",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "birth_year",
                schema: "investigation",
                table: "person",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "residence",
                schema: "investigation",
                table: "person",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            // Фигуранты, заведённые до перечня ролей (ТФ-ПЕР-01), получают «иная» (PersonRole.Other = 3):
            // 0 — не член перечня, а «объектом» или «связью» их может назначить только человек.
            migrationBuilder.AddColumn<int>(
                name: "role",
                schema: "investigation",
                table: "person",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<int>(
                name: "sex",
                schema: "investigation",
                table: "person",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "work_place",
                schema: "investigation",
                table: "person",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "initiator_details",
                schema: "investigation",
                table: "case_file",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "initiator_name",
                schema: "investigation",
                table: "case_file",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "initiator_phone",
                schema: "investigation",
                table: "case_file",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "initiator_position_id",
                schema: "investigation",
                table: "case_file",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "initiator_rank_id",
                schema: "investigation",
                table: "case_file",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "initiator_unit_id",
                schema: "investigation",
                table: "case_file",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "justification",
                schema: "investigation",
                table: "case_file",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                schema: "investigation",
                table: "case_file",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "task_notes",
                schema: "investigation",
                table: "case_file",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "task_number",
                schema: "investigation",
                table: "case_file",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reference_item",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reference_item", x => x.id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_person_birth_year_matches_date",
                schema: "investigation",
                table: "person",
                sql: "birth_date IS NULL OR birth_year = EXTRACT(YEAR FROM birth_date)");

            migrationBuilder.CreateIndex(
                name: "ix_case_file_initiator_position_id",
                schema: "investigation",
                table: "case_file",
                column: "initiator_position_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_file_initiator_rank_id",
                schema: "investigation",
                table: "case_file",
                column: "initiator_rank_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_file_initiator_unit_id",
                schema: "investigation",
                table: "case_file",
                column: "initiator_unit_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_case_file_task_requisites",
                schema: "investigation",
                table: "case_file",
                sql: "(kind = 4 AND initiator_unit_id IS NOT NULL AND coalesce(btrim(task_number), '') <> '' AND coalesce(btrim(justification), '') <> '' AND coalesce(btrim(purpose), '') <> '') OR (kind <> 4 AND task_number IS NULL AND initiator_unit_id IS NULL AND initiator_name IS NULL AND initiator_rank_id IS NULL AND initiator_position_id IS NULL AND initiator_phone IS NULL AND initiator_details IS NULL AND justification IS NULL AND purpose IS NULL AND task_notes IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_reference_item_kind_name",
                schema: "investigation",
                table: "reference_item",
                columns: new[] { "kind", "name" },
                unique: true);

            // Уникальность наименования без учёта регистра (ТФ-АДМ-07): хранилище сверяет её ILIKE до записи,
            // а этот индекс не даёт двум одновременным записям «Майор» и «майор» пройти обе. EF функциональный
            // индекс в модели не описывает — поэтому SQL; удаляется вместе с таблицей при откате.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_reference_item_kind_lower_name ON investigation.reference_item (kind, lower(name));");

            migrationBuilder.AddForeignKey(
                name: "fk_case_file_reference_items_initiator_position_id",
                schema: "investigation",
                table: "case_file",
                column: "initiator_position_id",
                principalSchema: "investigation",
                principalTable: "reference_item",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_case_file_reference_items_initiator_rank_id",
                schema: "investigation",
                table: "case_file",
                column: "initiator_rank_id",
                principalSchema: "investigation",
                principalTable: "reference_item",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_case_file_reference_items_initiator_unit_id",
                schema: "investigation",
                table: "case_file",
                column: "initiator_unit_id",
                principalSchema: "investigation",
                principalTable: "reference_item",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Откат молча уничтожил бы реквизиты заданий, а оставшиеся дела вида 4 прежний код не открыл бы
            // (валидатор вида их отклоняет). Поэтому при наличии заданий откат останавливается: сначала решить
            // судьбу этих дел (сменить вид или уничтожить по ADR-0025), затем откатывать.
            migrationBuilder.Sql(
                "DO $$ BEGIN IF EXISTS (SELECT 1 FROM investigation.case_file WHERE kind = 4) THEN "
                + "RAISE EXCEPTION 'Откат TaskRequisitesAndQuestionnaire невозможен: в базе есть дела вида «Задание по объекту».'; "
                + "END IF; END $$;");

            migrationBuilder.DropForeignKey(
                name: "fk_case_file_reference_items_initiator_position_id",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropForeignKey(
                name: "fk_case_file_reference_items_initiator_rank_id",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropForeignKey(
                name: "fk_case_file_reference_items_initiator_unit_id",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropTable(
                name: "reference_item",
                schema: "investigation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_person_birth_year_matches_date",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropIndex(
                name: "ix_case_file_initiator_position_id",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropIndex(
                name: "ix_case_file_initiator_rank_id",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropIndex(
                name: "ix_case_file_initiator_unit_id",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropCheckConstraint(
                name: "ck_case_file_task_requisites",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "alias",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "birth_date",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "birth_place",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "birth_year",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "residence",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "role",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "sex",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "work_place",
                schema: "investigation",
                table: "person");

            migrationBuilder.DropColumn(
                name: "initiator_details",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "initiator_name",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "initiator_phone",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "initiator_position_id",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "initiator_rank_id",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "initiator_unit_id",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "justification",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "purpose",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "task_notes",
                schema: "investigation",
                table: "case_file");

            migrationBuilder.DropColumn(
                name: "task_number",
                schema: "investigation",
                table: "case_file");
        }
    }
}
