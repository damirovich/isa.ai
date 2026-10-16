using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// Чат перешёл в пакет «Чат» (ADR-0052): переписка — в таблицах ядра <c>core.conversation</c> (миграция ядра
    /// <c>ConversationAreas</c>), поэтому своя переписка профиля (<c>case_chat</c>, <c>case_chat_message</c>, ADR-0051)
    /// удаляется. Перенос не делается сознательно: это тестовые диалоги показа 15.10.2026. Учёт материалов дел в корпусе
    /// (<c>case_corpus_entry</c>) остаётся — им пользуется порт чата профиля. Откат создаёт пустые таблицы.
    /// </summary>
    public partial class ChatToModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_chat_message",
                schema: "investigation");

            migrationBuilder.DropTable(
                name: "case_chat",
                schema: "investigation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "case_chat",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    case_id = table.Column<int>(type: "integer", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    owner_user_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_chat", x => x.id);
                    table.ForeignKey(
                        name: "fk_case_chat_cases_case_id",
                        column: x => x.case_id,
                        principalSchema: "investigation",
                        principalTable: "case_file",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "case_chat_message",
                schema: "investigation",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    chat_id = table.Column<int>(type: "integer", nullable: false),
                    check_json = table.Column<string>(type: "jsonb", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    sources_json = table.Column<string>(type: "jsonb", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_chat_message", x => x.id);
                    table.ForeignKey(
                        name: "fk_case_chat_message_case_chat_chat_id",
                        column: x => x.chat_id,
                        principalSchema: "investigation",
                        principalTable: "case_chat",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_case_chat_case_id_owner_user_id",
                schema: "investigation",
                table: "case_chat",
                columns: new[] { "case_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_case_chat_message_chat_id",
                schema: "investigation",
                table: "case_chat_message",
                column: "chat_id");
        }
    }
}
