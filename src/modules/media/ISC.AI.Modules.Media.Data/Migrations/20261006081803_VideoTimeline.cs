using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <summary>
    /// Лента видео (ADR-0038, ТФ-МЕД-11): у носителя <c>media.asset</c> — лента кадров (<c>filmstrip_stored_file_name</c> —
    /// картинка в категории хранилища <c>media-filmstrips</c>, <c>filmstrip_tile_count</c> — сколько кадров в ряду,
    /// <c>filmstrip_step_ms</c> — шаг между ними) и «встроенное» время начала записи из метаданных файла
    /// (<c>recorded_at</c>, проба ffprobe).
    /// </summary>
    /// <remarks>
    /// СУЩЕСТВУЮЩИЕ НОСИТЕЛИ: все четыре столбца nullable, старые строки получают <c>NULL</c>. Видео, проиндексированные
    /// до этой версии, получают ленту и время записи при «Переиндексировать»; до тех пор лента под проигрывателем
    /// показывает только шкалу и отметки лиц с подсказкой переиндексировать. Дата съёмки оператора (<c>captured_at</c>)
    /// не трогается — это другое поле: её подтверждает человек (ТФ-МЕД-17).
    /// </remarks>
    public partial class VideoTimeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "filmstrip_step_ms",
                schema: "media",
                table: "asset",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "filmstrip_stored_file_name",
                schema: "media",
                table: "asset",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "filmstrip_tile_count",
                schema: "media",
                table: "asset",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "recorded_at",
                schema: "media",
                table: "asset",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "filmstrip_step_ms",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "filmstrip_stored_file_name",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "filmstrip_tile_count",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "recorded_at",
                schema: "media",
                table: "asset");
        }
    }
}
