using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoStudio.Api.Migrations
{
    /// <inheritdoc />
    public partial class RenderPresetsAndPromptCompiler : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompiledPrompt",
                table: "RenderJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FrameNum",
                table: "RenderJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Preset",
                table: "RenderJobs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "SampleSteps",
                table: "RenderJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Seed",
                table: "RenderJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Size",
                table: "RenderJobs",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompiledPrompt",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "FrameNum",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "Preset",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "SampleSteps",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "Seed",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "Size",
                table: "RenderJobs");
        }
    }
}
