using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoStudio.Api.Migrations
{
    /// <inheritdoc />
    public partial class AudioPipelineJobsAndDialogueLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DialogueLineId",
                table: "RenderJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Emotion",
                table: "RenderJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "RenderJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Speaker",
                table: "RenderJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TextContent",
                table: "RenderJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Voice",
                table: "RenderJobs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DialogueLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SceneId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Speaker = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Emotion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EstimatedStartSecond = table.Column<int>(type: "int", nullable: false),
                    EstimatedEndSecond = table.Column<int>(type: "int", nullable: false),
                    AudioPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DialogueLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DialogueLines_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DialogueLines_Scenes_SceneId",
                        column: x => x.SceneId,
                        principalTable: "Scenes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RenderJobs_DialogueLineId",
                table: "RenderJobs",
                column: "DialogueLineId");

            migrationBuilder.CreateIndex(
                name: "IX_DialogueLines_ProjectId_SceneId_EstimatedStartSecond",
                table: "DialogueLines",
                columns: new[] { "ProjectId", "SceneId", "EstimatedStartSecond" });

            migrationBuilder.CreateIndex(
                name: "IX_DialogueLines_SceneId",
                table: "DialogueLines",
                column: "SceneId");

            migrationBuilder.AddForeignKey(
                name: "FK_RenderJobs_DialogueLines_DialogueLineId",
                table: "RenderJobs",
                column: "DialogueLineId",
                principalTable: "DialogueLines",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RenderJobs_DialogueLines_DialogueLineId",
                table: "RenderJobs");

            migrationBuilder.DropTable(
                name: "DialogueLines");

            migrationBuilder.DropIndex(
                name: "IX_RenderJobs_DialogueLineId",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "DialogueLineId",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "Emotion",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "Speaker",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "TextContent",
                table: "RenderJobs");

            migrationBuilder.DropColumn(
                name: "Voice",
                table: "RenderJobs");
        }
    }
}
