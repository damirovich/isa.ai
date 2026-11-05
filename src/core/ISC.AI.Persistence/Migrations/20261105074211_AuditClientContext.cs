using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <summary>
    /// Контекст записи журнала аудита (ТБ-080, ADR-0066): исход операции, IP-адрес клиента, сеанс входа и версия формулы
    /// хеша записи. Столбцы добавляются без перезаписи строк — триггер запрета изменения журнала (ТБ-031) не затрагивается.
    /// </summary>
    public partial class AuditClientContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_address",
                schema: "core",
                table: "audit_record",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "hash_version",
                schema: "core",
                table: "audit_record",
                type: "smallint",
                nullable: false,

                // Все записи до обновления посчитаны по формуле версии 1 — по ней они и проверяются (ТБ-080: прежние
                // записи — по прежней версии). Новые писатель журнала помечает версией 2 сам.
                defaultValue: (short)1);

            migrationBuilder.AddColumn<short>(
                name: "outcome",
                schema: "core",
                table: "audit_record",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                schema: "core",
                table: "audit_record",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_record_session_id",
                schema: "core",
                table: "audit_record",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Журнал неизменяем (ТБ-031): снятие столбцов стёрло бы исход, адрес и сеанс записей и сделало бы записи
            // версии 2 непроверяемыми. Откат разрешён, только пока таких записей нет; иначе версию откатывают
            // восстановлением базы из резервной копии.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM core.audit_record WHERE hash_version <> 1) THEN
                        RAISE EXCEPTION 'Откат AuditClientContext запрещён: в журнале аудита есть записи версии 2 (ТБ-031, ADR-0066). Откат версии — восстановлением из резервной копии.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ix_audit_record_session_id",
                schema: "core",
                table: "audit_record");

            migrationBuilder.DropColumn(
                name: "client_address",
                schema: "core",
                table: "audit_record");

            migrationBuilder.DropColumn(
                name: "hash_version",
                schema: "core",
                table: "audit_record");

            migrationBuilder.DropColumn(
                name: "outcome",
                schema: "core",
                table: "audit_record");

            migrationBuilder.DropColumn(
                name: "session_id",
                schema: "core",
                table: "audit_record");
        }
    }
}
