using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ISC.AI.Profile.Investigation.Data.Migrations
{
    /// <summary>
    /// ADR-0054: появление в режиме одного оператора — с одной подписью; вторая подпись (<c>verifier_user_id</c>)
    /// становится необязательной.
    /// </summary>
    public partial class AppearanceSingleSignature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "verifier_user_id",
                schema: "investigation",
                table: "appearance",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Откат при появлениях с одной подписью запрещён: подставить «вторую подпись» нечем, а 0 или эксперт исказили
            // бы журнал подтверждений (ТБ-072/073). Сначала решить судьбу таких появлений, затем откатывать.
            migrationBuilder.Sql(
                "DO $$ BEGIN IF EXISTS (SELECT 1 FROM investigation.appearance WHERE verifier_user_id IS NULL) THEN "
                + "RAISE EXCEPTION 'Есть появления с одной подписью (режим одного оператора, ADR-0054): откат невозможен без потери смысла подписи.'; "
                + "END IF; END $$;");

            migrationBuilder.AlterColumn<int>(
                name: "verifier_user_id",
                schema: "investigation",
                table: "appearance",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
