using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoStudio.Api.Migrations
{
    /// <inheritdoc />
    public partial class ProductionPlanningPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Shots_SceneId_Order",
                table: "Shots");

            migrationBuilder.DropIndex(
                name: "IX_Scenes_ProjectId_Order",
                table: "Scenes");

            migrationBuilder.AddColumn<string>(
                name: "Action",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AudioCue",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CameraMotion",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContinuityNotes",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "DurationSeconds",
                table: "Shots",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Index",
                table: "Shots",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ShotType",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DialogueLinesJson",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "EstimatedDurationSeconds",
                table: "Scenes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Index",
                table: "Scenes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Mood",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequiredCharactersJson",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TimeOfDay",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CameraStyle",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ColorPalette",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Genre",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LightingStyle",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NegativePrompt",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoryText",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetDurationSeconds",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VisualStylePrompt",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContinuityRulesJson",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Personality",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VisualPrompt",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VoiceStyle",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_Shots_SceneId_Index",
                table: "Shots",
                columns: new[] { "SceneId", "Index" });

            migrationBuilder.CreateIndex(
                name: "IX_Scenes_ProjectId_Index",
                table: "Scenes",
                columns: new[] { "ProjectId", "Index" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Shots_SceneId_Index",
                table: "Shots");

            migrationBuilder.DropIndex(
                name: "IX_Scenes_ProjectId_Index",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "Action",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "AudioCue",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "CameraMotion",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "ContinuityNotes",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "Index",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "ShotType",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "DialogueLinesJson",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "EstimatedDurationSeconds",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "Index",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "Mood",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "RequiredCharactersJson",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "TimeOfDay",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "CameraStyle",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ColorPalette",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Genre",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LightingStyle",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "NegativePrompt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "StoryText",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TargetDurationSeconds",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "VisualStylePrompt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ContinuityRulesJson",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "Personality",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "VisualPrompt",
                table: "Characters");

            migrationBuilder.DropColumn(
                name: "VoiceStyle",
                table: "Characters");

            migrationBuilder.CreateIndex(
                name: "IX_Shots_SceneId_Order",
                table: "Shots",
                columns: new[] { "SceneId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Scenes_ProjectId_Order",
                table: "Scenes",
                columns: new[] { "ProjectId", "Order" });
        }
    }
}
