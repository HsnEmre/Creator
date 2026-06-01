using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VideoStudio.Api.Migrations
{
    /// <inheritdoc />
    public partial class ProductionPlanAudioCues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AudioCuesJson",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioCuesJson",
                table: "Projects");
        }
    }
}
