using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.DocFlow.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropPdfCopyStoredFileName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pdf_copy_stored_file_name",
                schema: "docflow",
                table: "document_file");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pdf_copy_stored_file_name",
                schema: "docflow",
                table: "document_file",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }
    }
}
