using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocLibraryManagement.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class UsuarioRolesCopia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AreasUltimoAcceso",
                table: "Usuarios",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RolesUltimoAcceso",
                table: "Usuarios",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AreasUltimoAcceso",
                table: "Usuarios");

            migrationBuilder.DropColumn(
                name: "RolesUltimoAcceso",
                table: "Usuarios");
        }
    }
}
