using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <summary>
    /// Покадровый просмотр и снимок кадра (ADR-0028): у носителя <c>media.asset</c> — параметры видеопотока по пробе
    /// ffprobe (<c>frame_rate</c> — нативная частота кадров, к/с; <c>frame_width</c>/<c>frame_height</c> — размер кадра
    /// после автоповорота) и происхождение производного носителя — снимка кадра (<c>source_asset_id</c> — видео-источник,
    /// <c>source_timestamp_ms</c> — момент записи). Частичный индекс по <c>source_asset_id</c> — для «снимки этого видео».
    /// </summary>
    /// <remarks>
    /// <para>
    /// СУЩЕСТВУЮЩИЕ НОСИТЕЛИ: все пять столбцов nullable, старые строки получают <c>NULL</c>. Для фото и аудио это верно
    /// по существу. Видео, проиндексированные до этой версии, получают частоту, размер и ТОЧНУЮ длительность при
    /// «Переиндексировать» (проба выполняется перед раскадровкой, <c>MediaStore.CompleteIndexingAsync</c>); до тех пор
    /// карточка показывает шаг 40 мс (25 к/с) с пометкой, а прежняя <c>duration_ms</c> (таймкод последнего кадра
    /// выборки) не трогается.
    /// </para>
    /// <para>
    /// <c>source_asset_id</c> — СЛАБАЯ ссылка БЕЗ внешнего ключа (как <c>uploaded_by_user_id</c>): уничтожение видео не
    /// должно каскадом и без собственной записи журнала снести снимок — у каждого носителя свой акт (ТБ-064/075,
    /// ADR-0025). Поэтому значение может указывать на уже уничтоженный носитель — это ожидаемо.
    /// </para>
    /// </remarks>
    public partial class VideoFrameInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "frame_height",
                schema: "media",
                table: "asset",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "frame_rate",
                schema: "media",
                table: "asset",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "frame_width",
                schema: "media",
                table: "asset",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "source_asset_id",
                schema: "media",
                table: "asset",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_timestamp_ms",
                schema: "media",
                table: "asset",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_asset_source_asset_id",
                schema: "media",
                table: "asset",
                column: "source_asset_id",
                filter: "source_asset_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_asset_source_asset_id",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "frame_height",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "frame_rate",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "frame_width",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "source_asset_id",
                schema: "media",
                table: "asset");

            migrationBuilder.DropColumn(
                name: "source_timestamp_ms",
                schema: "media",
                table: "asset");
        }
    }
}
