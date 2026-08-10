using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.DocFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class DocumentCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    reg_number = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    reg_date = table.Column<DateOnly>(type: "date", nullable: false),
                    type_id = table.Column<int>(type: "integer", nullable: false),
                    direction_flag = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    short_content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    full_text = table.Column<string>(type: "text", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: true),
                    inspector_user_id = table.Column<int>(type: "integer", nullable: true),
                    aggregated_status = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    registered_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document", x => x.id);
                    table.CheckConstraint("chk_document_aggregated_status", "aggregated_status IN (0, 1, 2, 3, 4, 5, 6)");
                    table.CheckConstraint("chk_document_direction_flag", "direction_flag IN (1, 2, 3)");
                    table.CheckConstraint("chk_document_priority", "priority IS NULL OR priority IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "fk_document_document_types_type_id",
                        column: x => x.type_id,
                        principalSchema: "docflow",
                        principalTable: "document_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_assignment",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    assignee_user_id = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    use_common_deadline = table.Column<bool>(type: "boolean", nullable: false),
                    controller_user_id = table.Column<int>(type: "integer", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_assignment", x => x.id);
                    table.CheckConstraint("chk_assignment_status", "status IN (1, 2, 3, 4, 5, 6, 7)");
                    table.ForeignKey(
                        name: "fk_document_assignment_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "docflow",
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_attachment",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    stored_file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_attachment", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_attachment_documents_document_id",
                        column: x => x.document_id,
                        principalSchema: "docflow",
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_file",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    document_id = table.Column<int>(type: "integer", nullable: false),
                    language = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    stored_file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    is_latest = table.Column<bool>(type: "boolean", nullable: false),
                    pdf_copy_stored_file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    uploaded_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_file", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_file_document_document_id",
                        column: x => x.document_id,
                        principalSchema: "docflow",
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assignment_status_history",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    assignment_id = table.Column<int>(type: "integer", nullable: false),
                    from_status = table.Column<int>(type: "integer", nullable: true),
                    to_status = table.Column<int>(type: "integer", nullable: false),
                    changed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignment_status_history", x => x.id);
                    table.ForeignKey(
                        name: "fk_assignment_status_history_document_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalSchema: "docflow",
                        principalTable: "document_assignment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deadline_extension",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    assignment_id = table.Column<int>(type: "integer", nullable: false),
                    old_deadline = table.Column<DateOnly>(type: "date", nullable: false),
                    new_deadline = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    initiated_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deadline_extension", x => x.id);
                    table.ForeignKey(
                        name: "fk_deadline_extension_document_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalSchema: "docflow",
                        principalTable: "document_assignment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "status_history_file",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_history_id = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    stored_file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_status_history_file", x => x.id);
                    table.ForeignKey(
                        name: "fk_status_history_file_assignment_status_history_status_histor",
                        column: x => x.status_history_id,
                        principalSchema: "docflow",
                        principalTable: "assignment_status_history",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deadline_extension_file",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    extension_id = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    stored_file_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    content_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    file_size = table.Column<long>(type: "bigint", nullable: false),
                    uploaded_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deadline_extension_file", x => x.id);
                    table.ForeignKey(
                        name: "fk_deadline_extension_file_deadline_extension_extension_id",
                        column: x => x.extension_id,
                        principalSchema: "docflow",
                        principalTable: "deadline_extension",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_assignment_status_history_assignment_id",
                schema: "docflow",
                table: "assignment_status_history",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_deadline_extension_assignment_id",
                schema: "docflow",
                table: "deadline_extension",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_deadline_extension_file_extension_id",
                schema: "docflow",
                table: "deadline_extension_file",
                column: "extension_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_aggregated_status_updated_at",
                schema: "docflow",
                table: "document",
                columns: new[] { "aggregated_status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_document_division_id_classification",
                schema: "docflow",
                table: "document",
                columns: new[] { "division_id", "classification" });

            migrationBuilder.CreateIndex(
                name: "ix_document_reg_date",
                schema: "docflow",
                table: "document",
                column: "reg_date");

            migrationBuilder.CreateIndex(
                name: "ix_document_reg_number",
                schema: "docflow",
                table: "document",
                column: "reg_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_type_id",
                schema: "docflow",
                table: "document",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_assignment_assignee_user_id",
                schema: "docflow",
                table: "document_assignment",
                column: "assignee_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_assignment_division_id",
                schema: "docflow",
                table: "document_assignment",
                column: "division_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_assignment_document_id",
                schema: "docflow",
                table: "document_assignment",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_attachment_document_id",
                schema: "docflow",
                table: "document_attachment",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_file_document_id_is_latest",
                schema: "docflow",
                table: "document_file",
                columns: new[] { "document_id", "is_latest" });

            migrationBuilder.CreateIndex(
                name: "ix_status_history_file_status_history_id",
                schema: "docflow",
                table: "status_history_file",
                column: "status_history_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deadline_extension_file",
                schema: "docflow");

            migrationBuilder.DropTable(
                name: "document_attachment",
                schema: "docflow");

            migrationBuilder.DropTable(
                name: "document_file",
                schema: "docflow");

            migrationBuilder.DropTable(
                name: "status_history_file",
                schema: "docflow");

            migrationBuilder.DropTable(
                name: "deadline_extension",
                schema: "docflow");

            migrationBuilder.DropTable(
                name: "assignment_status_history",
                schema: "docflow");

            migrationBuilder.DropTable(
                name: "document_assignment",
                schema: "docflow");

            migrationBuilder.DropTable(
                name: "document",
                schema: "docflow");
        }
    }
}
