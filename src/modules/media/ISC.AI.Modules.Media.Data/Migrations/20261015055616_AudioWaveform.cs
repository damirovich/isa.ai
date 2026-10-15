using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class AudioWaveform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "waveform_peaks",
                schema: "media",
                table: "asset",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "waveform_step_ms",
                schema: "media",
                table: "asset",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "waveform_version",
                schema: "media",
                table: "asset",
                type: "smallint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "waveform_peaks",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "waveform_step_ms",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "waveform_version",
                schema: "media",
                table: "asset");
        }
    }
}
