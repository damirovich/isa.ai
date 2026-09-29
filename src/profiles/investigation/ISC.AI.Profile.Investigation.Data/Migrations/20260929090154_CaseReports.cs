using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <inheritdoc />
    public partial class CaseReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "case_report",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    person_id = table.Column<int>(type: "integer", nullable: true),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    report_date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    current_revision = table.Column<int>(type: "integer", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_report", x => x.id);
                    table.ForeignKey(
                        name: "fk_case_report_case_file_case_id",
                        column: x => x.case_id,
                        principalSchema: "investigation",
                        principalTable: "case_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_case_report_persons_person_id",
                        column: x => x.person_id,
                        principalSchema: "investigation",
                        principalTable: "person",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "case_report_permit",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    report_id = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    requested_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    decided_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    decided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_report_permit", x => x.id);
                    table.CheckConstraint("ck_case_report_permit_reason", "btrim(reason) <> ''");
                    table.ForeignKey(
                        name: "fk_case_report_permit_case_report_report_id",
                        column: x => x.report_id,
                        principalSchema: "investigation",
                        principalTable: "case_report",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "case_report_revision",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    report_id = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    content_json = table.Column<string>(type: "jsonb", nullable: false),
                    author_user_id = table.Column<int>(type: "integer", nullable: true),
                    edit_reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    permit_id = table.Column<int>(type: "integer", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_report_revision", x => x.id);
                    table.ForeignKey(
                        name: "fk_case_report_revision_case_report_report_id",
                        column: x => x.report_id,
                        principalSchema: "investigation",
                        principalTable: "case_report",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_case_report_case_id_report_date",
                schema: "investigation",
                table: "case_report",
                columns: new[] { "case_id", "report_date" });

            migrationBuilder.CreateIndex(
                name: "ix_case_report_person_id",
                schema: "investigation",
                table: "case_report",
                column: "person_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_report_permit_report_id_requested_by_user_id",
                schema: "investigation",
                table: "case_report_permit",
                columns: new[] { "report_id", "requested_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_report_permit_status_requested_at",
                schema: "investigation",
                table: "case_report_permit",
                columns: new[] { "status", "requested_at" });

            migrationBuilder.CreateIndex(
                name: "ix_case_report_revision_report_id_number",
                schema: "investigation",
                table: "case_report_revision",
                columns: new[] { "report_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_report_permit",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "case_report_revision",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "case_report",
                schema: "investigation");
        }
    }
}
