using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class TranscriptSpeakers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "speaker",
                schema: "media",
                table: "transcript_segment",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "speaker_edited_at",
                schema: "media",
                table: "transcript_segment",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "speaker_edited_by_user_id",
                schema: "media",
                table: "transcript_segment",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "speaker_override",
                schema: "media",
                table: "transcript_segment",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "transcript_speaker_note",
                schema: "media",
                table: "asset",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "transcript_speaker_source",
                schema: "media",
                table: "asset",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.CreateTable(
                name: "transcript_speaker",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<short>(type: "smallint", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    merged_into = table.Column<short>(type: "smallint", nullable: true),
                    edited_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    edited_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transcript_speaker", x => x.id);
                    table.ForeignKey(
                        name: "fk_transcript_speaker_asset_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_transcript_speaker_asset_id_number",
                schema: "media",
                table: "transcript_speaker",
                columns: new[] { "asset_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "transcript_speaker",
                schema: "media");

            migrationBuilder.DropColumn(
                name: "speaker",
                schema: "media",
                table: "transcript_segment");

            migrationBuilder.DropColumn(
                name: "speaker_edited_at",
                schema: "media",
                table: "transcript_segment");

            migrationBuilder.DropColumn(
                name: "speaker_edited_by_user_id",
                schema: "media",
                table: "transcript_segment");

            migrationBuilder.DropColumn(
                name: "speaker_override",
                schema: "media",
                table: "transcript_segment");

            migrationBuilder.DropColumn(
                name: "transcript_speaker_note",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "transcript_speaker_source",
                schema: "media",
                table: "asset");
        }
    }
}
