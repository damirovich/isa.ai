using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoreAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_record",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    subject_id = table.Column<int>(type: "integer", nullable: true),
                    action = table.Column<int>(type: "integer", nullable: false),
                    object_ref = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: true),
                    payload_sensitive = table.Column<string>(type: "text", nullable: true),
                    prev_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    record_hash = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_record", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_record_classification_division_id",
                schema: "core",
                table: "audit_record",
                columns: new[] { "classification", "division_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_record_occurred_at",
                schema: "core",
                table: "audit_record",
                column: "occurred_at");

            // Append-only на уровне БД (ТБ-031): триггер запрещает изменение/удаление записей журнала.
            // Защита-в-глубину поверх роли БД без прав UPDATE/DELETE.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION core.audit_record_no_modify() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'audit_record is append-only (TB-031): % not allowed', TG_OP;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_audit_record_no_modify
    BEFORE UPDATE OR DELETE ON core.audit_record
    FOR EACH ROW EXECUTE FUNCTION core.audit_record_no_modify();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_audit_record_no_modify ON core.audit_record;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS core.audit_record_no_modify();");

            migrationBuilder.DropTable(
                name: "audit_record",
                schema: "core");
        }
    }
}
