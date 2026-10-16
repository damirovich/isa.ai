using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <summary>
    /// Диалоги пакета чата (ADR-0052): у диалога — область профиля (<c>area</c>, <c>null</c> — область по умолчанию) и
    /// ключи уничтожения (<c>scope_keys</c>, пустой массив по умолчанию); у сообщения — источники ответа и итог проверки
    /// ссылок (jsonb). Прежние диалоги получают <c>area = null</c> и пустые ключи — остаются в области по умолчанию,
    /// то есть видны в чате «ИнспектораAI» как раньше. Данные не переписываются.
    /// </summary>
    public partial class ConversationAreas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "check_json",
                schema: "core",
                table: "conversation_message",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sources_json",
                schema: "core",
                table: "conversation_message",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "area",
                schema: "core",
                table: "conversation",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "scope_keys",
                schema: "core",
                table: "conversation",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.CreateIndex(
                name: "ix_conversation_subject_id_area",
                schema: "core",
                table: "conversation",
                columns: new[] { "subject_id", "area" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_conversation_subject_id_area",
                schema: "core",
                table: "conversation");

            migrationBuilder.DropColumn(
                name: "check_json",
                schema: "core",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "sources_json",
                schema: "core",
                table: "conversation_message");

            migrationBuilder.DropColumn(
                name: "area",
                schema: "core",
                table: "conversation");

            migrationBuilder.DropColumn(
                name: "scope_keys",
                schema: "core",
                table: "conversation");
        }
    }
}
