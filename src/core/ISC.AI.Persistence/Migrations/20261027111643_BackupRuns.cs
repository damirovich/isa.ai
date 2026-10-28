using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_run",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    started_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    folder_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    databases = table.Column<int>(type: "integer", nullable: false),
                    storage_files = table.Column<int>(type: "integer", nullable: true),
                    storage_bytes = table.Column<long>(type: "bigint", nullable: true),
                    bytes_written = table.Column<long>(type: "bigint", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    verify_status = table.Column<short>(type: "smallint", nullable: true),
                    verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verified_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    verify_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_run", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_backup_run_started_at",
                schema: "core",
                table: "backup_run",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ux_backup_run_single_running",
                schema: "core",
                table: "backup_run",
                column: "status",
                unique: true,
                filter: "status = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_run",
                schema: "core");
        }
    }
}
