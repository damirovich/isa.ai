using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.DocFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentReassignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assignment_reassignment",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    assignment_id = table.Column<int>(type: "integer", nullable: false),
                    from_user_id = table.Column<int>(type: "integer", nullable: true),
                    to_user_id = table.Column<int>(type: "integer", nullable: false),
                    changed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignment_reassignment", x => x.id);
                    table.ForeignKey(
                        name: "fk_assignment_reassignment_document_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalSchema: "docflow",
                        principalTable: "document_assignment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_assignment_document_id_division_id",
                schema: "docflow",
                table: "document_assignment",
                columns: new[] { "document_id", "division_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assignment_reassignment_assignment_id",
                schema: "docflow",
                table: "assignment_reassignment",
                column: "assignment_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assignment_reassignment",
                schema: "docflow");

            migrationBuilder.DropIndex(
                name: "ix_document_assignment_document_id_division_id",
                schema: "docflow",
                table: "document_assignment");
        }
    }
}
