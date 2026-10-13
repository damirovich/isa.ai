using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// Перенос баз Access (ADR-0043 п. 6): таблица <c>access_import_record</c> — какая запись какого файла базы
    /// (SHA-256) уже перенесена и в какое дело и фигуранта. Уникальность (файл, таблица, код) не даёт повтору
    /// переноса завести дубли; каскад от дела — уничтожение дела (ADR-0025) уносит и соответствия.
    /// </summary>
    public partial class AccessImportRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "access_import_record",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    file_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source_table = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    source_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    person_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_import_record", x => x.id);
                    table.ForeignKey(
                        name: "fk_access_import_record_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "investigation",
                        principalTable: "case_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_access_import_record_case_id",
                schema: "investigation",
                table: "access_import_record",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_access_import_record_file_sha256_source_table_source_key",
                schema: "investigation",
                table: "access_import_record",
                columns: new[] { "file_sha256", "source_table", "source_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access_import_record",
                schema: "investigation");
        }
    }
}
