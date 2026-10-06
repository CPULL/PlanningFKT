using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace minerva.planningfkt.Migrations
{
    /// <inheritdoc />
    public partial class NonRinnova : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NonRinnova",
                table: "Therapies",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NonRinnova",
                table: "Therapies");
        }
    }
}
