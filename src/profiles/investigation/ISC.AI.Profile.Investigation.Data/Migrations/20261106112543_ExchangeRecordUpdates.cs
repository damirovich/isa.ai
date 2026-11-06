using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// Обмен между офисами, версия 0.4 (ТФ-ОБМ-02, ADR-0068 v0.4): перенос изменений уже принятых записей — снимок записи
    /// источника в строке карты приёма (трёхстороннее сравнение по полям), в строке конфликта — вид записи и поля конфликта
    /// изменений, решения «принять версию источника» / «оставить свою» только у конфликта изменений (ограничение
    /// ck_exchange_conflict_decision_kind), единственность строки — по источнику, виду конфликта, виду записи и номеру; в
    /// журнале приёмов — обновлено записей и конфликтов изменений. Прежние данные не меняются: у строк карты снимка нет
    /// (принятые до 0.4 — правило в ADR), у прежних конфликтов вид записи 0, их решения 1–3 — ограничение выполняется.
    /// </summary>
    public partial class ExchangeRecordUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_exchange_import_conflict_source_instance_kind_source_id",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.AddColumn<string>(
                name: "snapshot",
                schema: "investigation",
                table: "exchange_source_record",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "changes",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "record_kind",
                schema: "investigation",
                table: "exchange_import_conflict",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<int>(
                name: "records_updated",
                schema: "investigation",
                table: "exchange_import",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "update_conflicts",
                schema: "investigation",
                table: "exchange_import",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "ix_exchange_import_conflict_source_instance_kind_record_kind_s",
                schema: "investigation",
                table: "exchange_import_conflict",
                columns: new[] { "source_instance", "kind", "record_kind", "source_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_exchange_conflict_decision_kind",
                schema: "investigation",
                table: "exchange_import_conflict",
                sql: "decision IS NULL OR (kind IN (1, 2) AND decision IN (1, 2, 3)) OR (kind = 3 AND decision IN (4, 5))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_exchange_import_conflict_source_instance_kind_record_kind_s",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropCheckConstraint(
                name: "ck_exchange_conflict_decision_kind",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "snapshot",
                schema: "investigation",
                table: "exchange_source_record");

            migrationBuilder.DropColumn(
                name: "changes",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "record_kind",
                schema: "investigation",
                table: "exchange_import_conflict");

            migrationBuilder.DropColumn(
                name: "records_updated",
                schema: "investigation",
                table: "exchange_import");

            migrationBuilder.DropColumn(
                name: "update_conflicts",
                schema: "investigation",
                table: "exchange_import");

            migrationBuilder.CreateIndex(
                name: "ix_exchange_import_conflict_source_instance_kind_source_id",
                schema: "investigation",
                table: "exchange_import_conflict",
                columns: new[] { "source_instance", "kind", "source_id" },
                unique: true);
        }
    }
}
