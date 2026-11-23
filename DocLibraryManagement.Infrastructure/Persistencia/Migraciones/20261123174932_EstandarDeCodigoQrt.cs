using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocLibraryManagement.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class EstandarDeCodigoQrt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documentos_TipoDocumentoId_AreaId_Consecutivo",
                table: "Documentos");

            migrationBuilder.AddColumn<string>(
                name: "Codigo",
                table: "SubAreas",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "CodigoDocumento",
                table: "Notificaciones",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Codigo",
                table: "Documentos",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(30)",
                oldMaxLength: 30);

            migrationBuilder.CreateIndex(
                name: "UX_SubArea_CodigoPorArea",
                table: "SubAreas",
                columns: new[] { "AreaId", "Codigo" },
                unique: true,
                filter: "[Codigo] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TipoDocumentoId_AreaId_SubAreaId_Consecutivo",
                table: "Documentos",
                columns: new[] { "TipoDocumentoId", "AreaId", "SubAreaId", "Consecutivo" },
                unique: true,
                filter: "[SubAreaId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_SubArea_CodigoPorArea",
                table: "SubAreas");

            migrationBuilder.DropIndex(
                name: "IX_Documentos_TipoDocumentoId_AreaId_SubAreaId_Consecutivo",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "Codigo",
                table: "SubAreas");

            migrationBuilder.AlterColumn<string>(
                name: "CodigoDocumento",
                table: "Notificaciones",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(60)",
                oldMaxLength: 60,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Codigo",
                table: "Documentos",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(60)",
                oldMaxLength: 60);

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TipoDocumentoId_AreaId_Consecutivo",
                table: "Documentos",
                columns: new[] { "TipoDocumentoId", "AreaId", "Consecutivo" },
                unique: true);
        }
    }
}
