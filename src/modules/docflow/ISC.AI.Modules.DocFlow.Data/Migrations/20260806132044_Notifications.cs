using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.DocFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification",
                schema: "docflow",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    recipient_user_id = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    message_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    arguments_json = table.Column<string>(type: "text", nullable: false),
                    document_id = table.Column<int>(type: "integer", nullable: true),
                    assignment_id = table.Column<int>(type: "integer", nullable: true),
                    comment_id = table.Column<int>(type: "integer", nullable: true),
                    about_deadline = table.Column<DateOnly>(type: "date", nullable: true),
                    is_read = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_document_assignment_assignment_id",
                        column: x => x.assignment_id,
                        principalSchema: "docflow",
                        principalTable: "document_assignment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_notification_document_comment_comment_id",
                        column: x => x.comment_id,
                        principalSchema: "docflow",
                        principalTable: "document_comment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_notification_document_document_id",
                        column: x => x.document_id,
                        principalSchema: "docflow",
                        principalTable: "document",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_assignment_id_type_about_deadline",
                schema: "docflow",
                table: "notification",
                columns: new[] { "assignment_id", "type", "about_deadline" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_comment_id",
                schema: "docflow",
                table: "notification",
                column: "comment_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_document_id",
                schema: "docflow",
                table: "notification",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_recipient_user_id_is_read_created_at",
                schema: "docflow",
                table: "notification",
                columns: new[] { "recipient_user_id", "is_read", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification",
                schema: "docflow");
        }
    }
}
