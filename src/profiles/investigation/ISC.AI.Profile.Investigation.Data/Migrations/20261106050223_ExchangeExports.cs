using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// Журнал выгрузок пакетов обмена между офисами и файлы носителей, ушедшие успешными выгрузками (ТФ-ОБМ-01, ADR-0068).
    /// Новые таблицы, прежние данные не меняются.
    /// </summary>
    public partial class ExchangeExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exchange_export",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    started_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    month = table.Column<int>(type: "integer", nullable: false),
                    case_numbers = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    media_kinds = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    package_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    cases = table.Column<int>(type: "integer", nullable: false),
                    persons = table.Column<int>(type: "integer", nullable: false),
                    links = table.Column<int>(type: "integer", nullable: false),
                    reports = table.Column<int>(type: "integer", nullable: false),
                    assets = table.Column<int>(type: "integer", nullable: false),
                    files = table.Column<int>(type: "integer", nullable: false),
                    appearances = table.Column<int>(type: "integer", nullable: false),
                    bytes_written = table.Column<long>(type: "bigint", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    resend_files = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_export", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "exchange_export_file",
                schema: "investigation",
                columns: table => new
                {
                    export_id = table.Column<int>(type: "integer", nullable: false),
                    asset_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_export_file", x => new { x.export_id, x.asset_id });
                    table.ForeignKey(
                        name: "fk_exchange_export_file_exchange_export_export_id",
                        column: x => x.export_id,
                        principalSchema: "investigation",
                        principalTable: "exchange_export",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_exchange_export_started_at",
                schema: "investigation",
                table: "exchange_export",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_export_status",
                schema: "investigation",
                table: "exchange_export",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_export_file_asset_id",
                schema: "investigation",
                table: "exchange_export_file",
                column: "asset_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exchange_export_file",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "exchange_export",
                schema: "investigation");
        }
    }
}
