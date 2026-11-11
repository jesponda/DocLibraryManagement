using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocLibraryManagement.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class SolicitudesSinCambios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolicitudesSinCambios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RevisionId = table.Column<int>(type: "int", nullable: false),
                    SolicitanteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Comentario = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FechaSolicitudUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaProximaAnteriorUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaProximaNuevaUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecididaPorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ComentarioDecision = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    FechaDecisionUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesSinCambios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SolicitudesSinCambios_Revisiones_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "Revisiones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesSinCambios_Usuarios_DecididaPorId",
                        column: x => x.DecididaPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesSinCambios_Usuarios_SolicitanteId",
                        column: x => x.SolicitanteId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesSinCambios_DecididaPorId",
                table: "SolicitudesSinCambios",
                column: "DecididaPorId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesSinCambios_RevisionId",
                table: "SolicitudesSinCambios",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesSinCambios_SolicitanteId",
                table: "SolicitudesSinCambios",
                column: "SolicitanteId");

            migrationBuilder.CreateIndex(
                name: "UX_SinCambios_UnicaPendiente",
                table: "SolicitudesSinCambios",
                column: "RevisionId",
                unique: true,
                filter: "[Estado] = 'Pendiente'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolicitudesSinCambios");
        }
    }
}
