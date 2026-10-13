using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <summary>
    /// Документы архива в импорте (ADR-0042): реквизиты документа в строке задания (тип, рег. номер, дата, название) и
    /// номер созданного документа документооборота; индекс по (дело, SHA-256) — повтор файла в то же дело без второго документа.
    /// </summary>
    public partial class MediaImportDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "document_id",
                schema: "media",
                table: "import_item",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "document_reg_date",
                schema: "media",
                table: "import_item",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "document_reg_date_from_file",
                schema: "media",
                table: "import_item",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "document_reg_number",
                schema: "media",
                table: "import_item",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "document_title",
                schema: "media",
                table: "import_item",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "document_type_id",
                schema: "media",
                table: "import_item",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_item_case_id_content_sha256",
                schema: "media",
                table: "import_item",
                columns: new[] { "case_id", "content_sha256" },
                filter: "document_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_import_item_case_id_content_sha256",
                schema: "media",
                table: "import_item");

            migrationBuilder.DropColumn(
                name: "document_id",
                schema: "media",
                table: "import_item");

            migrationBuilder.DropColumn(
                name: "document_reg_date",
                schema: "media",
                table: "import_item");

            migrationBuilder.DropColumn(
                name: "document_reg_date_from_file",
                schema: "media",
                table: "import_item");

            migrationBuilder.DropColumn(
                name: "document_reg_number",
                schema: "media",
                table: "import_item");

            migrationBuilder.DropColumn(
                name: "document_title",
                schema: "media",
                table: "import_item");

            migrationBuilder.DropColumn(
                name: "document_type_id",
                schema: "media",
                table: "import_item");
        }
    }
}
