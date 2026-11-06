using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// Приём пакетов обмена между офисами (ТФ-ОБМ-02, ADR-0068 v0.2): журнал приёмов, карта «запись источника → своя
    /// запись» (уникальна по источнику, виду и номеру — повторный приём дублей не заводит) и конфликты для очереди
    /// Руководителя. Новые таблицы, прежние данные не меняются.
    /// </summary>
    public partial class ExchangeImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exchange_import",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    started_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    package_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_instance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    manifest_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    gateway_confirmed = table.Column<bool>(type: "boolean", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: true),
                    cases_created = table.Column<int>(type: "integer", nullable: false),
                    persons_created = table.Column<int>(type: "integer", nullable: false),
                    events_created = table.Column<int>(type: "integer", nullable: false),
                    reports_created = table.Column<int>(type: "integer", nullable: false),
                    assets_created = table.Column<int>(type: "integer", nullable: false),
                    files_received = table.Column<int>(type: "integer", nullable: false),
                    bytes_received = table.Column<long>(type: "bigint", nullable: false),
                    already_imported = table.Column<int>(type: "integer", nullable: false),
                    conflicts = table.Column<int>(type: "integer", nullable: false),
                    deferred = table.Column<int>(type: "integer", nullable: false),
                    missing_files = table.Column<int>(type: "integer", nullable: false),
                    rejected = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_import", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "exchange_import_conflict",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    import_id = table.Column<int>(type: "integer", nullable: false),
                    source_instance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    source_id = table.Column<int>(type: "integer", nullable: false),
                    local_id = table.Column<int>(type: "integer", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    label = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_import_conflict", x => x.id);
                    table.ForeignKey(
                        name: "fk_exchange_import_conflict_exchange_import_import_id",
                        column: x => x.import_id,
                        principalSchema: "investigation",
                        principalTable: "exchange_import",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "exchange_source_record",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_instance = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<short>(type: "smallint", nullable: false),
                    source_id = table.Column<int>(type: "integer", nullable: false),
                    local_id = table.Column<int>(type: "integer", nullable: false),
                    import_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_exchange_source_record", x => x.id);
                    table.ForeignKey(
                        name: "fk_exchange_source_record_exchange_import_import_id",
                        column: x => x.import_id,
                        principalSchema: "investigation",
                        principalTable: "exchange_import",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_exchange_import_package_name",
                schema: "investigation",
                table: "exchange_import",
                column: "package_name");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_import_started_at",
                schema: "investigation",
                table: "exchange_import",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_import_status",
                schema: "investigation",
                table: "exchange_import",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_import_conflict_import_id",
                schema: "investigation",
                table: "exchange_import_conflict",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_import_conflict_source_instance_kind_source_id",
                schema: "investigation",
                table: "exchange_import_conflict",
                columns: new[] { "source_instance", "kind", "source_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_exchange_import_conflict_status",
                schema: "investigation",
                table: "exchange_import_conflict",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_source_record_import_id",
                schema: "investigation",
                table: "exchange_source_record",
                column: "import_id");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_source_record_kind_local_id",
                schema: "investigation",
                table: "exchange_source_record",
                columns: new[] { "kind", "local_id" });

            migrationBuilder.CreateIndex(
                name: "ix_exchange_source_record_source_instance_kind_source_id",
                schema: "investigation",
                table: "exchange_source_record",
                columns: new[] { "source_instance", "kind", "source_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "exchange_import_conflict",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "exchange_source_record",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "exchange_import",
                schema: "investigation");
        }
    }
}
