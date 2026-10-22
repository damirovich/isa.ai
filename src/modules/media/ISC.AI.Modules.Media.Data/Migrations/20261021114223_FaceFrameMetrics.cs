using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class FaceFrameMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "frame_eye_distance",
                schema: "media",
                table: "face",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "frame_lighting",
                schema: "media",
                table: "face",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "frame_pitch",
                schema: "media",
                table: "face",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "frame_roll",
                schema: "media",
                table: "face",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "frame_score",
                schema: "media",
                table: "face",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "frame_sharpness",
                schema: "media",
                table: "face",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "frame_yaw",
                schema: "media",
                table: "face",
                type: "real",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "frame_eye_distance",
                schema: "media",
                table: "face");

            migrationBuilder.DropColumn(
                name: "frame_lighting",
                schema: "media",
                table: "face");

            migrationBuilder.DropColumn(
                name: "frame_pitch",
                schema: "media",
                table: "face");

            migrationBuilder.DropColumn(
                name: "frame_roll",
                schema: "media",
                table: "face");

            migrationBuilder.DropColumn(
                name: "frame_score",
                schema: "media",
                table: "face");

            migrationBuilder.DropColumn(
                name: "frame_sharpness",
                schema: "media",
                table: "face");

            migrationBuilder.DropColumn(
                name: "frame_yaw",
                schema: "media",
                table: "face");
        }
    }
}
