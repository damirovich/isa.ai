using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <summary>
    /// Шкала грифов из пяти уровней (ADR-0030): 0 — без грифа, 1 — ДСП, 2 — секретно, 3 — совершенно
    /// секретно, 4 — особой важности. Значения прежней шкалы 0–9 выше 4 во всех таблицах схемы
    /// <c>core</c> приводятся к 4: по новой шкале всё, что выше «особой важности», и есть «особая
    /// важность» (иначе строку не увидел бы ни один допуск после приведения допусков к шкале).
    /// Журнал аудита (<c>core.audit_record</c>) НЕ трогается: он неизменяем (ТБ-030, триггер и цепочка хешей);
    /// его записи выше 4 при чтении трактуются как «особой важности» (<c>AuditReader</c>).
    /// </summary>
    /// <remarks>
    /// Колонки находятся по имени (<c>classification</c>, <c>max_classification</c>, <c>result_classification</c>)
    /// во всех таблицах схемы — чтобы не пропустить таблицу, добавленную позже, и не держать список вручную.
    /// Откат невозможен: прежние значения 5–9 не сохраняются (по смыслу они совпадают с 4).
    /// </remarks>
    public partial class ClassificationScaleFiveLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE c record;
                BEGIN
                    FOR c IN
                        SELECT col.table_schema, col.table_name, col.column_name
                        FROM information_schema.columns col
                        JOIN information_schema.tables t
                          ON t.table_schema = col.table_schema AND t.table_name = col.table_name
                        WHERE col.table_schema = 'core'
                          AND t.table_type = 'BASE TABLE'
                          AND col.column_name IN ('classification', 'max_classification', 'result_classification')
                          AND col.data_type IN ('smallint', 'integer')
                          AND col.table_name <> 'audit_record'
                    LOOP
                        EXECUTE format('UPDATE %I.%I SET %I = 4 WHERE %I > 4',
                            c.table_schema, c.table_name, c.column_name, c.column_name);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Данные не восстанавливаются: см. remarks.
        }
    }
}
