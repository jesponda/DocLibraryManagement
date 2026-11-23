using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocLibraryManagement.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CuentasLocales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AreasAsignadas",
                table: "Usuarios",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "BloqueadoHastaUtc",
                table: "Usuarios",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DebeCambiarContrasena",
                table: "Usuarios",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "HashContrasena",
                table: "Usuarios",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntentosFallidos",
                table: "Usuarios",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RolesAsignados",
                table: "Usuarios",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "SelloSeguridad",
                table: "Usuarios",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AreasAsignadas",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "BloqueadoHastaUtc",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "DebeCambiarContrasena",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "HashContrasena",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "IntentosFallidos",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "RolesAsignados",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "SelloSeguridad",
                table: "Usuarios");
        }
    }
}
