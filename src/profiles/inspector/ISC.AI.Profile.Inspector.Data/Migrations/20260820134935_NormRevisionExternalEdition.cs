using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Inspector.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormRevisionExternalEdition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_edition_id",
                schema: "inspector",
                table: "norm_revision",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_norm_revision_norm_id_external_edition_id",
                schema: "inspector",
                table: "norm_revision",
                columns: new[] { "norm_id", "external_edition_id" },
                unique: true,
                filter: "external_edition_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_norm_revision_norm_id_external_edition_id",
                schema: "inspector",
                table: "norm_revision");

            migrationBuilder.DropColumn(
                name: "external_edition_id",
                schema: "inspector",
                table: "norm_revision");
        }
    }
}
