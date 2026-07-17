using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Inspector.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddViolationDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "division",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_division", x => x.id);
                    table.ForeignKey(
                        name: "fk_division_division_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "inspector",
                        principalTable: "division",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "violation_category",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    parent_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_violation_category", x => x.id);
                    table.ForeignKey(
                        name: "fk_violation_category_violation_category_parent_id",
                        column: x => x.parent_id,
                        principalSchema: "inspector",
                        principalTable: "violation_category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "violation",
                schema: "inspector",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    category_id = table.Column<int>(type: "integer", nullable: false),
                    severity = table.Column<int>(type: "integer", nullable: false),
                    detected_at = table.Column<DateOnly>(type: "date", nullable: false),
                    remediation_status = table.Column<int>(type: "integer", nullable: false),
                    source_doc_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_assignment_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reference_doc_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    cause = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    recommendation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_violation", x => x.id);
                    table.ForeignKey(
                        name: "fk_violation_division_division_id",
                        column: x => x.division_id,
                        principalSchema: "inspector",
                        principalTable: "division",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_violation_violation_category_category_id",
                        column: x => x.category_id,
                        principalSchema: "inspector",
                        principalTable: "violation_category",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_division_code",
                schema: "inspector",
                table: "division",
                column: "code");

            migrationBuilder.CreateIndex(
                name: "ix_division_parent_id",
                schema: "inspector",
                table: "division",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_violation_category_id",
                schema: "inspector",
                table: "violation",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_violation_division_id_detected_at",
                schema: "inspector",
                table: "violation",
                columns: new[] { "division_id", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "ix_violation_remediation_status",
                schema: "inspector",
                table: "violation",
                column: "remediation_status");

            migrationBuilder.CreateIndex(
                name: "ix_violation_category_parent_id",
                schema: "inspector",
                table: "violation_category",
                column: "parent_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "violation",
                schema: "inspector");

            migrationBuilder.DropTable(
                name: "division",
                schema: "inspector");

            migrationBuilder.DropTable(
                name: "violation_category",
                schema: "inspector");
        }
    }
}
