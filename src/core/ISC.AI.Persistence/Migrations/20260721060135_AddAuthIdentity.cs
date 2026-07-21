using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_clearance_user_id",
                schema: "core",
                table: "clearance");

            migrationBuilder.DropIndex(
                name: "ix_app_user_user_name",
                schema: "core",
                table: "app_user");

            // Пустой массив по умолчанию: у существующих допусков подразделения не расширяются
            // задним числом (default-deny — ТБ-021), их задаёт администратор явно.
            migrationBuilder.AddColumn<List<int>>(
                name: "division_scope",
                schema: "core",
                table: "clearance",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                schema: "core",
                table: "app_user",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_clearance_user_id",
                schema: "core",
                table: "clearance",
                column: "user_id",
                unique: true,
                filter: "is_deleted = false");

            migrationBuilder.CreateIndex(
                name: "ix_app_user_external_id",
                schema: "core",
                table: "app_user",
                column: "external_id",
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_app_user_user_name",
                schema: "core",
                table: "app_user",
                column: "user_name",
                unique: true,
                filter: "is_deleted = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_clearance_user_id",
                schema: "core",
                table: "clearance");

            migrationBuilder.DropIndex(
                name: "ix_app_user_external_id",
                schema: "core",
                table: "app_user");

            migrationBuilder.DropIndex(
                name: "ix_app_user_user_name",
                schema: "core",
                table: "app_user");

            migrationBuilder.DropColumn(
                name: "division_scope",
                schema: "core",
                table: "clearance");

            migrationBuilder.DropColumn(
                name: "external_id",
                schema: "core",
                table: "app_user");

            migrationBuilder.CreateIndex(
                name: "ix_clearance_user_id",
                schema: "core",
                table: "clearance",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_app_user_user_name",
                schema: "core",
                table: "app_user",
                column: "user_name",
                unique: true);
        }
    }
}
