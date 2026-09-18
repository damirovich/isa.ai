using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ISC.AI.Modules.Media.Data.Migrations
{
    /// <inheritdoc />
    public partial class SearchSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_session",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    case_id = table.Column<int>(type: "integer", nullable: false),
                    authorization_ref = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    scope = table.Column<int>(type: "integer", nullable: false),
                    case_ids = table.Column<int[]>(type: "integer[]", nullable: false),
                    probe_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    probe_face_id = table.Column<int>(type: "integer", nullable: true),
                    probe_crop_stored_file_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    top_k = table.Column<int>(type: "integer", nullable: false),
                    max_cosine_distance = table.Column<double>(type: "double precision", nullable: true),
                    detector_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    embedder_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    hnsw_ef_search = table.Column<int>(type: "integer", nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    requested_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_session", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "search_candidate",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    session_id = table.Column<int>(type: "integer", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    face_id = table.Column<int>(type: "integer", nullable: false),
                    asset_id = table.Column<int>(type: "integer", nullable: false),
                    frame_index = table.Column<int>(type: "integer", nullable: true),
                    frame_timestamp_ms = table.Column<long>(type: "bigint", nullable: true),
                    cosine_distance = table.Column<double>(type: "double precision", nullable: false),
                    crop_stored_file_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    classification = table.Column<short>(type: "smallint", nullable: false),
                    division_id = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    person_ref = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_candidate", x => x.id);
                    table.ForeignKey(
                        name: "fk_search_candidate_search_sessions_session_id",
                        column: x => x.session_id,
                        principalSchema: "media",
                        principalTable: "search_session",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "verification_decision",
                schema: "media",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    candidate_id = table.Column<int>(type: "integer", nullable: false),
                    subject_id = table.Column<int>(type: "integer", nullable: false),
                    stage = table.Column<int>(type: "integer", nullable: false),
                    verdict = table.Column<int>(type: "integer", nullable: false),
                    rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    decided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_verification_decision", x => x.id);
                    table.ForeignKey(
                        name: "fk_verification_decision_search_candidate_candidate_id",
                        column: x => x.candidate_id,
                        principalSchema: "media",
                        principalTable: "search_candidate",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_candidate_classification_division_id",
                schema: "media",
                table: "search_candidate",
                columns: new[] { "classification", "division_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_candidate_session_id_rank",
                schema: "media",
                table: "search_candidate",
                columns: new[] { "session_id", "rank" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_candidate_status",
                schema: "media",
                table: "search_candidate",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_search_session_case_id",
                schema: "media",
                table: "search_session",
                column: "case_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_session_classification_division_id",
                schema: "media",
                table: "search_session",
                columns: new[] { "classification", "division_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_session_probe_crop_stored_file_name",
                schema: "media",
                table: "search_session",
                column: "probe_crop_stored_file_name",
                unique: true,
                filter: "probe_crop_stored_file_name IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_verification_decision_candidate_id_stage",
                schema: "media",
                table: "verification_decision",
                columns: new[] { "candidate_id", "stage" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "verification_decision",
                schema: "media");

            migrationBuilder.DropTable(
                name: "search_candidate",
                schema: "media");

            migrationBuilder.DropTable(
                name: "search_session",
                schema: "media");
        }
    }
}
