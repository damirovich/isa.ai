using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <summary>
    /// Пересоздание двух уникальных индексов: имя вырезки пробы уникально только для пробы-изображения
    /// (<c>probe_face_id IS NULL</c> — повторный поиск по одному лицу носителя не упирается в индекс, ТФ-ПЛ-03),
    /// а ключ дедупликации носителей — (подразделение, ГРИФ, хеш): гриф носителя равен грифу дела (ТБ-070/074).
    /// </summary>
    public partial class SearchSessionProbeAndDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_search_session_probe_crop_stored_file_name",
                schema: "media",
                table: "search_session");

            migrationBuilder.DropIndex(
                name: "ix_asset_division_id_content_hash",
                schema: "media",
                table: "asset");

            migrationBuilder.CreateIndex(
                name: "ix_search_session_probe_crop_stored_file_name",
                schema: "media",
                table: "search_session",
                column: "probe_crop_stored_file_name",
                unique: true,
                filter: "probe_crop_stored_file_name IS NOT NULL AND probe_face_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_asset_division_id_classification_content_hash",
                schema: "media",
                table: "asset",
                columns: new[] { "division_id", "classification", "content_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_search_session_probe_crop_stored_file_name",
                schema: "media",
                table: "search_session");

            migrationBuilder.DropIndex(
                name: "ix_asset_division_id_classification_content_hash",
                schema: "media",
                table: "asset");

            migrationBuilder.CreateIndex(
                name: "ix_search_session_probe_crop_stored_file_name",
                schema: "media",
                table: "search_session",
                column: "probe_crop_stored_file_name",
                unique: true,
                filter: "probe_crop_stored_file_name IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_asset_division_id_content_hash",
                schema: "media",
                table: "asset",
                columns: new[] { "division_id", "content_hash" },
                unique: true);
        }
    }
}
