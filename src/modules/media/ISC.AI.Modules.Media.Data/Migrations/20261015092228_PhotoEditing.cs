using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhotoEditing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "edit_operations",
                schema: "media",
                table: "asset",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "annotation",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    x1 = table.Column<double>(type: "double precision", nullable: false),
                    y1 = table.Column<double>(type: "double precision", nullable: false),
                    x2 = table.Column<double>(type: "double precision", nullable: false),
                    y2 = table.Column<double>(type: "double precision", nullable: false),
                    text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    color = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    author_user_id = table.Column<int>(type: "integer", nullable: true),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_annotation", x => x.id);
                    table.ForeignKey(
                        name: "fk_annotation_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "media",
                        principalTable: "asset",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_annotation_asset_id",
                schema: "media",
                table: "annotation",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_annotation_classification_division_id",
                schema: "media",
                table: "annotation",
                columns: new[] { "classification", "division_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "annotation",
                schema: "media");

            migrationBuilder.DropColumn(
                name: "edit_operations",
                schema: "media",
                table: "asset");
        }
    }
}
