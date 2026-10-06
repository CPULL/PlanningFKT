using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace minerva.planningfkt.Migrations
{
    /// <inheritdoc />
    public partial class Fatturato : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TherapyPacketId",
                table: "Therapies",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TherapyPrCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    TherapyId = table.Column<int>(type: "int", nullable: false),
                    PrCodeId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TherapyPrCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TherapyPrCodes_PrCodes_PrCodeId",
                        column: x => x.PrCodeId,
                        principalTable: "PrCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TherapyPrCodes_Therapies_TherapyId",
                        column: x => x.TherapyId,
                        principalTable: "Therapies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Therapies_TherapyPacketId",
                table: "Therapies",
                column: "TherapyPacketId");

            migrationBuilder.CreateIndex(
                name: "IX_TherapyPrCodes_PrCodeId",
                table: "TherapyPrCodes",
                column: "PrCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_TherapyPrCodes_TherapyId",
                table: "TherapyPrCodes",
                column: "TherapyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Therapies_TherapyPackets_TherapyPacketId",
                table: "Therapies",
                column: "TherapyPacketId",
                principalTable: "TherapyPackets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Therapies_TherapyPackets_TherapyPacketId",
                table: "Therapies");

            migrationBuilder.DropTable(
                name: "TherapyPrCodes");

            migrationBuilder.DropIndex(
                name: "IX_Therapies_TherapyPacketId",
                table: "Therapies");

            migrationBuilder.DropColumn(
                name: "TherapyPacketId",
                table: "Therapies");
        }
    }
}
