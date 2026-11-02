using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Persistence.Migrations
{
    /// <summary>
    /// Блокировка учётной записи после неудачных входов подряд (ТБ-081, ADR-0065): <c>core.app_user.failed_login_count</c>
    /// (счётчик, у прежних учёток — 0) и <c>locked_out_at</c> (когда заблокирована; у прежних — пусто). Данные не
    /// переносятся: после накатки ни одна учётка не заблокирована.
    /// </summary>
    public partial class AccountLockout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "failed_login_count",
                schema: "core",
                table: "app_user",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "locked_out_at",
                schema: "core",
                table: "app_user",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failed_login_count",
                schema: "core",
                table: "app_user");

            migrationBuilder.DropColumn(
                name: "locked_out_at",
                schema: "core",
                table: "app_user");
        }
    }
}
