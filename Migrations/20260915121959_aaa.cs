using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace minerva.planningfkt.Migrations
{
    /// <inheritdoc />
    public partial class aaa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OverrideOperatingArea",
                table: "TherapistAvailabilities",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OverrideOperatingArea",
                table: "TherapistAvailabilities");
        }
    }
}
