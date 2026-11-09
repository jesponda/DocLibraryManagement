using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocLibraryManagement.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class PublicacionYDueno : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Revisiones_DocumentoId_Mayor_Menor",
                table: "Revisiones");

            migrationBuilder.AddColumn<Guid>(
                name: "DuenoId",
                table: "Areas",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Revisiones_DocumentoId_Mayor_Menor",
                table: "Revisiones",
                columns: new[] { "DocumentoId", "Mayor", "Menor" },
                unique: true,
                filter: "[Estado] <> 'Cancelado'");

            migrationBuilder.CreateIndex(
                name: "IX_Areas_DuenoId",
                table: "Areas",
                column: "DuenoId");

            migrationBuilder.AddForeignKey(
                name: "FK_Areas_Usuarios_DuenoId",
                table: "Areas",
                column: "DuenoId",
                principalTable: "Usuarios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Areas_Usuarios_DuenoId",
                table: "Areas");

            migrationBuilder.DropIndex(
                name: "IX_Revisiones_DocumentoId_Mayor_Menor",
                table: "Revisiones");

            migrationBuilder.DropIndex(
                name: "IX_Areas_DuenoId",
                table: "Areas");

            migrationBuilder.DropColumn(
                name: "DuenoId",
                table: "Areas");

            migrationBuilder.CreateIndex(
                name: "IX_Revisiones_DocumentoId_Mayor_Menor",
                table: "Revisiones",
                columns: new[] { "DocumentoId", "Mayor", "Menor" },
                unique: true);
        }
    }
}
