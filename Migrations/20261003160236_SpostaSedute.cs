using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace minerva.planningfkt.Migrations
{
    /// <inheritdoc />
    public partial class SpostaSedute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WasAbsent",
                table: "TherapySlots",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WasAbsent",
                table: "TherapySlots");
        }
    }
}
