using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoStudio.Api.Migrations
{
    /// <inheritdoc />
    public partial class DirectorContinuityPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AssemblyExtensionAllowed",
                table: "Shots",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "CharacterLockPrompt",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentShotVisualState",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForbiddenDriftTerms",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvolvedCharacterIdsJson",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "KeyframeContinuityPrompt",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationId",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationLockPrompt",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NextShotSetup",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousShotVisualState",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecommendedRenderDurationMode",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SceneAnchorPrompt",
                table: "Shots",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForbiddenLocationDrift",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationContinuityPrompt",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationId",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SceneAnchorPrompt",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoryStateAfter",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoryStateBefore",
                table: "Scenes",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActBreakdownJson",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "BeatSheetJson",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "CharacterBibleJson",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "DirectorTreatment",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationBibleJson",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "QualityGoal",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "Balanced");

            migrationBuilder.AddColumn<string>(
                name: "RenderStrategyRecommendationJson",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "TimelineContinuityJson",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "VisualContinuityRulesJson",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "CharacterBibleJson",
                table: "Characters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssemblyExtensionAllowed",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "CharacterLockPrompt",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "CurrentShotVisualState",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "ForbiddenDriftTerms",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "InvolvedCharacterIdsJson",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "KeyframeContinuityPrompt",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "LocationLockPrompt",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "NextShotSetup",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "PreviousShotVisualState",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "RecommendedRenderDurationMode",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "SceneAnchorPrompt",
                table: "Shots");

            migrationBuilder.DropColumn(
                name: "ForbiddenLocationDrift",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "LocationContinuityPrompt",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "SceneAnchorPrompt",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "StoryStateAfter",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "StoryStateBefore",
                table: "Scenes");

            migrationBuilder.DropColumn(
                name: "ActBreakdownJson",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "BeatSheetJson",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "CharacterBibleJson",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DirectorTreatment",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "LocationBibleJson",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "QualityGoal",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RenderStrategyRecommendationJson",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "TimelineContinuityJson",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "VisualContinuityRulesJson",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "CharacterBibleJson",
                table: "Characters");
        }
    }
}
