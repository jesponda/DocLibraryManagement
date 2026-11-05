using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocLibraryManagement.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class FlujoYArchivos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Revisiones",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InstanciasFlujo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RevisionId = table.Column<int>(type: "int", nullable: false),
                    Numero = table.Column<int>(type: "int", nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FechaInicioUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FechaFinUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IniciadoPorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstanciasFlujo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstanciasFlujo_Revisiones_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "Revisiones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InstanciasFlujo_Usuarios_IniciadoPorId",
                        column: x => x.IniciadoPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PasosPlantilla",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TipoDocumentoId = table.Column<int>(type: "int", nullable: false),
                    Orden = table.Column<int>(type: "int", nullable: false),
                    Clase = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasosPlantilla", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PasosPlantilla_TiposDocumento_TipoDocumentoId",
                        column: x => x.TipoDocumentoId,
                        principalTable: "TiposDocumento",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PasosInstancia",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstanciaFlujoId = table.Column<int>(type: "int", nullable: false),
                    Orden = table.Column<int>(type: "int", nullable: false),
                    Clase = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AsignadoAId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DecididoPorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FechaDecisionUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Comentario = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasosInstancia", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PasosInstancia_InstanciasFlujo_InstanciaFlujoId",
                        column: x => x.InstanciaFlujoId,
                        principalTable: "InstanciasFlujo",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PasosInstancia_Usuarios_AsignadoAId",
                        column: x => x.AsignadoAId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PasosInstancia_Usuarios_DecididoPorId",
                        column: x => x.DecididoPorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstanciasFlujo_IniciadoPorId",
                table: "InstanciasFlujo",
                column: "IniciadoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_InstanciasFlujo_RevisionId_Numero",
                table: "InstanciasFlujo",
                columns: new[] { "RevisionId", "Numero" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Flujo_UnicoEnCurso",
                table: "InstanciasFlujo",
                column: "RevisionId",
                unique: true,
                filter: "[Estado] = 'EnCurso'");

            migrationBuilder.CreateIndex(
                name: "IX_PasosInstancia_AsignadoAId",
                table: "PasosInstancia",
                column: "AsignadoAId");

            migrationBuilder.CreateIndex(
                name: "IX_PasosInstancia_DecididoPorId",
                table: "PasosInstancia",
                column: "DecididoPorId");

            migrationBuilder.CreateIndex(
                name: "IX_PasosInstancia_InstanciaFlujoId_Orden",
                table: "PasosInstancia",
                columns: new[] { "InstanciaFlujoId", "Orden" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PasosPlantilla_TipoDocumentoId_Orden",
                table: "PasosPlantilla",
                columns: new[] { "TipoDocumentoId", "Orden" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasosInstancia");

            migrationBuilder.DropTable(
                name: "PasosPlantilla");

            migrationBuilder.DropTable(
                name: "InstanciasFlujo");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Revisiones");
        }
    }
}
