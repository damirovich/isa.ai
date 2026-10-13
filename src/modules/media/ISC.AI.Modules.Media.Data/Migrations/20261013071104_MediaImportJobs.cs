using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <summary>
    /// Пакетный импорт архива с описью (ADR-0041): задания (<c>media.import_job</c>) и их строки — файлы архива
    /// (<c>media.import_item</c>) с подтверждёнными грифом и подразделением дела, состоянием приёма и подачи в
    /// обработку; у носителя — ссылка на строку импорта, которой он принят (<c>asset.import_item_id</c>, ТБ-077).
    /// </summary>
    public partial class MediaImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "import_item_id",
                schema: "media",
                table: "asset",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "import_job",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    folder_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    manifest_file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    manifest_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    operator_user_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    stop_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    total_count = table.Column<int>(type: "integer", nullable: false),
                    accepted_count = table.Column<int>(type: "integer", nullable: false),
                    duplicate_count = table.Column<int>(type: "integer", nullable: false),
                    rejected_count = table.Column<int>(type: "integer", nullable: false),
                    bytes_total = table.Column<long>(type: "bigint", nullable: false),
                    bytes_done = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_job", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "import_item",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    job_id = table.Column<int>(type: "integer", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    relative_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    case_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    captured_date_only = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    place = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    expected_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    asset_id = table.Column<int>(type: "integer", nullable: true),
                    content_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    processing = table.Column<int>(type: "integer", nullable: false),
                    index_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    transcript_task_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_item_asset_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_import_item_import_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "media",
                        principalTable: "import_job",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_asset_import_item_id",
                schema: "media",
                table: "asset",
                column: "import_item_id",
                filter: "import_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_import_item_asset_id",
                schema: "media",
                table: "import_item",
                column: "asset_id",
                filter: "asset_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_import_item_classification_division_id",
                schema: "media",
                table: "import_item",
                columns: new[] { "classification", "division_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_item_job_id_row_number",
                schema: "media",
                table: "import_item",
                columns: new[] { "job_id", "row_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_item_job_id_status_row_number",
                schema: "media",
                table: "import_item",
                columns: new[] { "job_id", "status", "row_number" });

            migrationBuilder.CreateIndex(
                name: "ix_import_item_processing_job_id_row_number",
                schema: "media",
                table: "import_item",
                columns: new[] { "processing", "job_id", "row_number" });

            migrationBuilder.CreateIndex(
                name: "ix_import_job_status_id",
                schema: "media",
                table: "import_job",
                columns: new[] { "status", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_item",
                schema: "media");

            migrationBuilder.DropTable(
                name: "import_job",
                schema: "media");

            migrationBuilder.DropIndex(
                name: "ix_asset_import_item_id",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "import_item_id",
                schema: "media",
                table: "asset");
        }
    }
}
