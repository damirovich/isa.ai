using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Inspector.Data.Migrations
{
    /// <inheritdoc />
    public partial class NormIdentifierUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_legal_norm_identifier",
                schema: "inspector",
                table: "legal_norm");

            migrationBuilder.CreateIndex(
                name: "ix_legal_norm_identifier",
                schema: "inspector",
                table: "legal_norm",
                column: "identifier",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_legal_norm_identifier",
                schema: "inspector",
                table: "legal_norm");

            migrationBuilder.CreateIndex(
                name: "ix_legal_norm_identifier",
                schema: "inspector",
                table: "legal_norm",
                column: "identifier");
        }
    }
}
