using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoStudio.Api.Migrations
{
    /// <inheritdoc />
    [Migration("20260605093000_RenderJobDurationMetadata")]
    public partial class RenderJobDurationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActualFrameNum",
                table: "RenderJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ExpectedRawClipDurationSeconds",
                table: "RenderJobs",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ProbedRawClipDurationSeconds",
                table: "RenderJobs",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RawDurationCoveragePercent",
                table: "RenderJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenderDurationMode",
                table: "RenderJobs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "FastPreview");

            migrationBuilder.AddColumn<int>(
                name: "RequestedFrameNum",
                table: "RenderJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RequestedShotDurationSeconds",
                table: "RenderJobs",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualFrameNum",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "ExpectedRawClipDurationSeconds",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "ProbedRawClipDurationSeconds",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "RawDurationCoveragePercent",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "RenderDurationMode",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "RequestedFrameNum",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "RequestedShotDurationSeconds",
                table: "RenderJobs");
        }
    }
}
