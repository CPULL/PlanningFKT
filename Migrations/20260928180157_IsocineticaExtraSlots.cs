using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace minerva.planningfkt.Migrations
{
    /// <inheritdoc />
    public partial class IsocineticaExtraSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExtraSlots",
                table: "TherapySlots",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtraSlots",
                table: "TherapySlots");
        }
    }
}
