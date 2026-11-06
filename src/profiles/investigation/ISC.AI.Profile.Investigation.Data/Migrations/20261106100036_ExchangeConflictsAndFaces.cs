using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// Обмен между офисами, этап 3 (ТФ-ОБМ-02, ADR-0068 v0.3): решения Руководителя в строке конфликта (описание записи
    /// источника, решение, кто и когда, применение), ожидание сопоставления лиц для эталонов и появлений пакета, у появления —
    /// пометка источника и провенанс, а сеанс, кандидат и первая подпись необязательны только у импортированного
    /// (ограничение ck_appearance_origin); в журнале приёмов — объединено, пропущено, перенесено и сверка. Прежние данные не
    /// меняются: у всех существующих появлений сеанс, кандидат и подпись заданы — ограничение выполняется.
    /// </summary>
    public partial class ExchangeConflictsAndFaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "applied_at",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "applied_import_id",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "decided_at",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "decided_by_user_id",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "decision",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_comment",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "decision_local_id",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_number",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "details",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "faces_linked",
                schema: "investigation",
                table: "exchange_import",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "faces_not_found",
                schema: "investigation",
                table: "exchange_import",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "merged",
                schema: "investigation",
                table: "exchange_import",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "reconciliation",
                schema: "investigation",
                table: "exchange_import",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "reconciliation_ok",
                schema: "investigation",
                table: "exchange_import",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "skipped_by_decision",
                schema: "investigation",
                table: "exchange_import",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<int>(
                name: "search_session_id",
                schema: "investigation",
                table: "appearance",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "expert_user_id",
                schema: "investigation",
                table: "appearance",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "candidate_id",
                schema: "investigation",
                table: "appearance",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "source_instance",
                schema: "investigation",
                table: "appearance",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_provenance",
                schema: "investigation",
                table: "appearance",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "exchange_pending_face",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_instance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    source_id = table.Column<int>(type: "integer", nullable: false),
                    import_id = table.Column<int>(type: "integer", nullable: false),
                    package_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    local_id = table.Column<int>(type: "integer", nullable: true),
                    checked_index_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_pending_face", x => x.id);
                    table.CheckConstraint("ck_exchange_pending_face_kind", "kind IN (10, 11)");
                    table.ForeignKey(
                        name: "fk_exchange_pending_face_exchange_import_import_id",
                        column: x => x.import_id,
                        principalSchema: "investigation",
                        principalTable: "exchange_import",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_exchange_conflict_decided",
                schema: "investigation",
                table: "exchange_import_conflict",
                sql: "status NOT IN (3, 4) OR decision IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_exchange_conflict_new_case",
                schema: "investigation",
                table: "exchange_import_conflict",
                sql: "decision IS NULL OR decision <> 2 OR kind = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_appearance_origin",
                schema: "investigation",
                table: "appearance",
                sql: "(search_session_id IS NOT NULL AND candidate_id IS NOT NULL AND expert_user_id IS NOT NULL) OR source_instance IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_pending_face_import_id",
                schema: "investigation",
                table: "exchange_pending_face",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_pending_face_source_instance_kind_source_id",
                schema: "investigation",
                table: "exchange_pending_face",
                columns: new[] { "source_instance", "kind", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_exchange_pending_face_status",
                schema: "investigation",
                table: "exchange_pending_face",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exchange_pending_face",
                schema: "investigation");

            migrationBuilder.DropCheckConstraint(
                name: "ck_exchange_conflict_decided",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropCheckConstraint(
                name: "ck_exchange_conflict_new_case",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropCheckConstraint(
                name: "ck_appearance_origin",
                schema: "investigation",
                table: "appearance");

            migrationBuilder.DropColumn(
                name: "applied_at",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "applied_import_id",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "decided_at",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "decided_by_user_id",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "decision",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "decision_comment",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "decision_local_id",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "decision_number",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "details",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "faces_linked",
                schema: "investigation",
                table: "exchange_import");

            migrationBuilder.DropColumn(
                name: "faces_not_found",
                schema: "investigation",
                table: "exchange_import");

            migrationBuilder.DropColumn(
                name: "merged",
                schema: "investigation",
                table: "exchange_import");

            migrationBuilder.DropColumn(
                name: "reconciliation",
                schema: "investigation",
                table: "exchange_import");

            migrationBuilder.DropColumn(
                name: "reconciliation_ok",
                schema: "investigation",
                table: "exchange_import");

            migrationBuilder.DropColumn(
                name: "skipped_by_decision",
                schema: "investigation",
                table: "exchange_import");

            migrationBuilder.DropColumn(
                name: "source_instance",
                schema: "investigation",
                table: "appearance");

            migrationBuilder.DropColumn(
                name: "source_provenance",
                schema: "investigation",
                table: "appearance");

            migrationBuilder.AlterColumn<int>(
                name: "search_session_id",
                schema: "investigation",
                table: "appearance",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "expert_user_id",
                schema: "investigation",
                table: "appearance",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "candidate_id",
                schema: "investigation",
                table: "appearance",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
