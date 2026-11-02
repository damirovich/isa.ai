using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <summary>
    /// Части незавершённой индексации видео (ТНД-007, ADR-0064): <c>media.index_checkpoint</c> — лица полностью
    /// обработанных кадров с шаблонами, чтобы повтор после сбоя или перезапуска не распознавал их заново. Таблица служебная
    /// и после накатки пустая — данных миграция не переносит. Каскад от носителя: уничтожение (ТБ-064) уносит и части.
    /// </summary>
    public partial class IndexCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "index_checkpoint",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    run_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    first_frame_index = table.Column<int>(type: "integer", nullable: false),
                    last_frame_index = table.Column<int>(type: "integer", nullable: false),
                    face_count = table.Column<int>(type: "integer", nullable: false),
                    faces = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_index_checkpoint", x => x.id);
                    table.ForeignKey(
                        name: "fk_index_checkpoint_asset_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_index_checkpoint_asset_id_first_frame_index",
                schema: "media",
                table: "index_checkpoint",
                columns: new[] { "asset_id", "first_frame_index" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "index_checkpoint",
                schema: "media");
        }
    }
}
