using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <summary>
    /// Расшифровка речи (ADR-0026): таблица фрагментов <c>media.transcript_segment</c> (гриф и подразделение
    /// носителя — решётка ТБ-020/021 по строке; FK на носитель <c>ON DELETE CASCADE</c> — уничтожение носителя
    /// или дела сносит расшифровку, ТБ-064, ADR-0025) и состояние расшифровки у носителя.
    /// </summary>
    /// <remarks>
    /// СУЩЕСТВУЮЩИЕ НОСИТЕЛИ получают <c>transcript_status = 0</c> — «неприменима» (<c>TranscriptStatus.NotApplicable</c>)
    /// через значение по умолчанию добавляемого столбца. Для изображений это верно по существу. Видео, загруженные
    /// до этой версии, тоже помечаются «неприменима», а НЕ «в очереди»: их никто не ставил на расшифровку, и
    /// статус «ожидает» висел бы вечно, вводя в заблуждение. Расшифровать старое видео можно кнопкой
    /// «Расшифровать» в карточке (сценарий RetranscribeMediaCommand). Аудио до этой версии не принималось вовсе.
    /// </remarks>
    public partial class AudioTranscripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "transcribed_at",
                schema: "media",
                table: "asset",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transcriber_version",
                schema: "media",
                table: "asset",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transcript_error",
                schema: "media",
                table: "asset",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "transcript_status",
                schema: "media",
                table: "asset",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "transcript_segment",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    index = table.Column<int>(type: "integer", nullable: false),
                    start_ms = table.Column<long>(type: "bigint", nullable: false),
                    end_ms = table.Column<long>(type: "bigint", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    model_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcript_segment", x => x.id);
                    table.ForeignKey(
                        name: "fk_transcript_segment_asset_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transcript_segment_asset_id_index",
                schema: "media",
                table: "transcript_segment",
                columns: new[] { "asset_id", "index" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transcript_segment_classification_division_id",
                schema: "media",
                table: "transcript_segment",
                columns: new[] { "classification", "division_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transcript_segment",
                schema: "media");

            migrationBuilder.DropColumn(
                name: "transcribed_at",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "transcriber_version",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "transcript_error",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "transcript_status",
                schema: "media",
                table: "asset");
        }
    }
}
