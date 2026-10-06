using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace minerva.planningfkt.Migrations
{
    /// <inheritdoc />
    public partial class PagatoPrCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Pagato",
                table: "Therapies",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PrCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Code = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Cost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ModDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ModUser = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrCodes", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "PrCodes",
                columns: new[] { "Id", "Code", "Cost", "Description", "ModDate", "ModUser" },
                values: new object[,]
                {
                    { 1, "PR1", 202.30m, "Paresi del nervo facciale", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 2, "PR2", 202.30m, "Mononeuriti dell'arto superiore e inferiore di origine meccanica e non meccanica e neuriti multiple", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 3, "PR3", 195.10m, "Monoartriti solo in caso di riacutizzazioni, comprese le riacutizzazioni localizzate in corso di artrite reumatoide", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 4, "PR4A", 231.60m, "Frattura della clavicola (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 5, "PR4B", 231.60m, "Frattura della scapola (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 6, "PR4C", 231.60m, "Frattura omero compreso paletta omerale (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 7, "PR4D", 231.60m, "Frattura radio, ulna (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 8, "PR4E", 231.60m, "Frattura ossa carpo e metacarpo (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 9, "PR4F", 231.60m, "Frattura di una o più falangi della mano (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 10, "PR5A", 231.60m, "Lussazione della spalla (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 11, "PR5B", 231.60m, "Lussazione del gomito (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 12, "PR5C", 231.60m, "Lussazione del polso (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 13, "PR5D", 231.60m, "Lussazione delle dita della mano (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 14, "PR6A", 231.60m, "Distorsione della spalla e del braccio (compreso instabilità gleno omerale) (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 15, "PR6B", 231.60m, "Distorsione del gomito e dell'avambraccio (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 16, "PR6C", 231.60m, "Distorsione del polso e della mano (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 17, "PR7", 214.50m, "Lesioni della spalla (compreso rottura atraumatica della cuffia dei rotatori)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 18, "PR8", 152.90m, "Traumatismo dei nervi della spalla e del braccio (compreso avambraccio, polso e mano) - esiti funzionali", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 19, "PR9", 214.50m, "Traumatismo dei muscoli e dei tendini della spalla e del braccio (compreso avambraccio, polso e mano) esiti funzionali", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 20, "PR10", 228.20m, "Frattura collo femore e di altre parti del femore di tipo traumatico (in soggetto < 65 anni) - esiti funzionali", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 21, "PR11A", 228.20m, "Frattura della rotula", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 22, "PR11B", 228.20m, "Frattura della tibia e del perone", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 23, "PR11C", 228.20m, "Frattura della caviglia", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 24, "PR11D", 228.20m, "Frattura di una o più ossa del tarso e metatarso", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 25, "PR11E", 228.20m, "Frattura di una o più falangi del piede", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 26, "PR12A", 228.20m, "Lussazione di ginocchio (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 27, "PR12B", 228.20m, "Lussazione della caviglia (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 28, "PR12C", 228.20m, "Lussazione del piede (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 29, "PR13A", 230.40m, "Distorsione dell'anca e della coscia (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 30, "PR13B", 230.40m, "Distorsione del ginocchio e della gamba (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 31, "PR13C", 230.40m, "Distorsione della caviglia e del piede (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 32, "PR14", 215.80m, "Disturbi della rotula, lesioni capsulo-legamentose del ginocchio e del menisco (trattamento post chirurgico)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 33, "PR15", 152.90m, "Traumatismo di nervi dell'anca e della coscia - compreso gamba, caviglia e piede - (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 34, "PR16", 215.80m, "Traumatismo di muscoli e tendini della gamba (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 35, "PR17", 221.30m, "Frattura della colonna vertebrale senza menzione di lesione del midollo spinale", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 36, "PR18", 221.30m, "Frattura del bacino (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 37, "PR19", 231.60m, "Lussazione della mandibola (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 38, "PR20", 221.30m, "Distorsione del rachide, della regione sacroiliaca e di altre parti non specificate del dorso (esiti funzionali)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 39, "PR21", 255.40m, "Disturbi dei plessi e delle radici nervose (associata a stenosi del canale vertebrale cervicale o lombare) solo in presenza di limitazione algofunzionale", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 40, "PR22", 152.90m, "Compressione dei plessi e delle radici nervose (associata a sindrome brachialgica o sciatalgica) (solo in presenza di limitazione algofunzionale persistente)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 41, "PR23", 255.40m, "Limitazione algofunzionale da mal di schiena acuto/postacuto persistente (2-3 settimane) e/o recidivante", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 42, "PR24", 214.50m, "Alterazioni di continuità dell'osso (mancata consolidazione)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 43, "PR25A", 152.90m, "Calcificazione e ossificazione dei muscoli (come complicanza di eventi traumatici recenti)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 44, "PR25B", 152.90m, "Malattie dei tessuti molli correlate ad uso eccessivo, pressione", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 45, "PR26A", 255.40m, "Esiti di intervento per impianto di artroprotesi ginocchio", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 46, "PR26B", 255.40m, "Esiti di intervento per impianto di artroprotesi spalla", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 47, "PR26C", 255.40m, "Esiti di intervento per impianto di artroprotesi anca", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 48, "PR27", 231.60m, "Trauma da parto del sistema nervoso periferico senza lesione complessa del tronco nervoso (paralisi ostetrica)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 49, "PR28", 231.60m, "Deformità congenita dell'anca seguito da trattamento ortopedico (displasia dell'anca)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 50, "PR29", 231.60m, "Deformazione dei piedi (piede torto congenito)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 51, "PR30", 152.90m, "Torcicollo miogeno congenito del neonato", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 52, "PR31I", 152.90m, "Disturbi della voce di origine funzionale (compresi noduli, polipi, edemi e prolassi) e negli esiti di cordectomia semplice e paralisi cordale ricorrenziale (terapia individuale)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 53, "PR31G", 51.00m, "Disturbi della voce di origine funzionale (compresi noduli, polipi, edemi e prolassi) e negli esiti di cordectomia semplice e paralisi cordale ricorrenziale (terapia di gruppo)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 54, "PR32I", 152.90m, "Disturbi semplici del linguaggio (late talkers, disturbi fonetici semplici) individuale", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 55, "PR32G", 51.00m, "Disturbi semplici del linguaggio (late talkers, disturbi fonetici semplici) di gruppo", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 56, "PR33I", 152.90m, "Disturbi miofunzionali odontofacciali (inclusa deglutizione atipica) terapia individuale", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 57, "PR33G", 51.00m, "Disturbi miofunzionali odontofacciali (inclusa deglutizione atipica) (terapia di gruppo costituita da 3 a max 5 persone)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 58, "PR34I", 152.90m, "Disturbo respiratorio semplice senza insufficienza respiratoria (terapia individuale)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 59, "PR34G", 51.00m, "Disturbo respiratorio semplice senza insufficienza respiratoria (terapia di gruppo)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 60, "PR35A", 152.90m, "Linfedemi degli arti (post mastectomia arto superiore o per altre condizioni patologiche arto inferiore) (costo per n. 5 accessi di 60 min.)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 61, "PR35B", 162.30m, "Incontinenza urinaria e/o fecale", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 62, "PR36A", 195.10m, "Disturbi delle sinovie con limitazione algofunzionale", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 63, "PR36B", 195.10m, "Borsopatie con limitazione algofunzionale", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 64, "PR36C", 195.10m, "Entesopatie con limitazione algofunzionale", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 },
                    { 65, "PR37", 60.00m, "Attività motoria per esiti stabilizzati da patologie croniche (tariffa per 1 pacchetto di 20 accessi)", new DateTime(2026, 10, 3, 0, 0, 0, 0, DateTimeKind.Unspecified), 1 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrCodes_Code",
                table: "PrCodes",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrCodes");

            migrationBuilder.DropColumn(
                name: "Pagato",
                table: "Therapies");
        }
    }
}
