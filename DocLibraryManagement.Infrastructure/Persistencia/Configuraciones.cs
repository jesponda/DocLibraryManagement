using DocLibraryManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DocLibraryManagement.Infrastructure.Persistencia;

internal sealed class AreaConfig : IEntityTypeConfiguration<Area>
{
    public void Configure(EntityTypeBuilder<Area> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Codigo).HasMaxLength(10).IsRequired();
        b.Property(x => x.Nombre).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Codigo).IsUnique();
        b.HasOne(x => x.Dueno).WithMany().HasForeignKey(x => x.DuenoId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubAreaConfig : IEntityTypeConfiguration<SubArea>
{
    public void Configure(EntityTypeBuilder<SubArea> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Nombre).HasMaxLength(200).IsRequired();
        b.HasOne(x => x.Area).WithMany(a => a.SubAreas).HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AreaId, x.Nombre }).IsUnique();
    }
}

internal sealed class TipoDocumentoConfig : IEntityTypeConfiguration<TipoDocumento>
{
    public void Configure(EntityTypeBuilder<TipoDocumento> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Codigo).HasMaxLength(10).IsRequired();
        b.Property(x => x.Nombre).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Codigo).IsUnique();
        b.ToTable(t => t.HasCheckConstraint("CK_TipoDocumento_MesesRevision", "[MesesRevision] > 0"));
    }
}

internal sealed class AreaTipoDocumentoConfig : IEntityTypeConfiguration<AreaTipoDocumento>
{
    public void Configure(EntityTypeBuilder<AreaTipoDocumento> b)
    {
        b.HasKey(x => new { x.AreaId, x.TipoDocumentoId });
        b.HasOne(x => x.Area).WithMany(a => a.TiposDocumento).HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.TipoDocumento).WithMany().HasForeignKey(x => x.TipoDocumentoId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SubAreaTipoDocumentoConfig : IEntityTypeConfiguration<SubAreaTipoDocumento>
{
    public void Configure(EntityTypeBuilder<SubAreaTipoDocumento> b)
    {
        b.HasKey(x => new { x.SubAreaId, x.TipoDocumentoId });
        b.HasOne(x => x.SubArea).WithMany(a => a.TiposDocumento).HasForeignKey(x => x.SubAreaId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.TipoDocumento).WithMany().HasForeignKey(x => x.TipoDocumentoId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UsuarioConfig : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Identificador).HasMaxLength(256).IsRequired();
        b.Property(x => x.NombreVisible).HasMaxLength(200).IsRequired();
        b.Property(x => x.Correo).HasMaxLength(256);
        b.Property(x => x.RolesUltimoAcceso).HasMaxLength(200).IsRequired();
        b.Property(x => x.AreasUltimoAcceso).HasMaxLength(1000).IsRequired();
        b.HasIndex(x => x.Identificador).IsUnique();
    }
}

internal sealed class DocumentoConfig : IEntityTypeConfiguration<Documento>
{
    public void Configure(EntityTypeBuilder<Documento> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Codigo).HasMaxLength(30).IsRequired();
        b.Property(x => x.Titulo).HasMaxLength(300).IsRequired();
        b.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20);

        b.HasOne(x => x.Area).WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SubArea).WithMany().HasForeignKey(x => x.SubAreaId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.TipoDocumento).WithMany().HasForeignKey(x => x.TipoDocumentoId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Responsable).WithMany().HasForeignKey(x => x.ResponsableId).OnDelete(DeleteBehavior.Restrict);

        // 6.1: el código y el consecutivo por (tipo, área) son únicos y no se reutilizan.
        b.HasIndex(x => x.Codigo).IsUnique();
        b.HasIndex(x => new { x.TipoDocumentoId, x.AreaId, x.Consecutivo }).IsUnique();
    }
}

internal sealed class RevisionDocumentoConfig : IEntityTypeConfiguration<RevisionDocumento>
{
    public void Configure(EntityTypeBuilder<RevisionDocumento> b)
    {
        b.HasKey(x => x.Id);
        b.Ignore(x => x.Etiqueta);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.MotivoCambio).HasMaxLength(1000).IsRequired();
        b.Property(x => x.DescripcionCambio).HasMaxLength(4000);
        b.Property(x => x.RutaArchivoFuente).HasMaxLength(500);
        b.Property(x => x.RutaArchivoPdf).HasMaxLength(500);
        b.Property(x => x.HashArchivoFuente).HasMaxLength(64);
        b.Property(x => x.HashArchivoPdf).HasMaxLength(64);

        b.HasOne(x => x.Documento).WithMany(d => d.Revisiones).HasForeignKey(x => x.DocumentoId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Autor).WithMany().HasForeignKey(x => x.AutorId).OnDelete(DeleteBehavior.Restrict);

        // Un borrador cancelado no consume número (7.1.3): el siguiente borrador puede volver a usar Rev.01.
        b.HasIndex(x => new { x.DocumentoId, x.Mayor, x.Menor })
            .IsUnique()
            .HasFilter("[Estado] <> 'Cancelado'");

        // Importante: ambos índices usan la misma columna. EF solo los distingue si se declaran
        // con nombre en HasIndex (HasDatabaseName no basta: el segundo reemplazaría al primero).

        // 7.1.4: una sola revisión Vigente por documento, garantizada en BD.
        b.HasIndex(x => x.DocumentoId, "UX_Revision_UnicaVigente")
            .IsUnique()
            .HasFilter("[Estado] = 'Vigente'");

        // 7.1.5: una sola revisión abierta por documento, garantizada en BD.
        b.HasIndex(x => x.DocumentoId, "UX_Revision_UnicaAbierta")
            .IsUnique()
            .HasFilter("[Estado] IN ('Borrador', 'EnRevision', 'EnAprobacion', 'Aprobado')");
    }
}

internal sealed class PasoPlantillaConfig : IEntityTypeConfiguration<PasoPlantilla>
{
    public void Configure(EntityTypeBuilder<PasoPlantilla> b)
    {
        b.ToTable("PasosPlantilla");
        b.HasKey(x => x.Id);
        b.Property(x => x.Clase).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Nombre).HasMaxLength(100).IsRequired();
        b.HasOne(x => x.TipoDocumento).WithMany().HasForeignKey(x => x.TipoDocumentoId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TipoDocumentoId, x.Orden }).IsUnique();
    }
}

internal sealed class InstanciaFlujoConfig : IEntityTypeConfiguration<InstanciaFlujo>
{
    public void Configure(EntityTypeBuilder<InstanciaFlujo> b)
    {
        b.ToTable("InstanciasFlujo");
        b.HasKey(x => x.Id);
        b.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.MotivoCierre).HasMaxLength(1000);
        b.HasOne(x => x.Revision).WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.IniciadoPor).WithMany().HasForeignKey(x => x.IniciadoPorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.RevisionId, x.Numero }).IsUnique();

        // Una sola ejecución del flujo activa por revisión, garantizada en BD.
        b.HasIndex(x => x.RevisionId, "UX_Flujo_UnicoEnCurso")
            .IsUnique()
            .HasFilter("[Estado] = 'EnCurso'");
    }
}

internal sealed class PasoInstanciaConfig : IEntityTypeConfiguration<PasoInstancia>
{
    public void Configure(EntityTypeBuilder<PasoInstancia> b)
    {
        b.ToTable("PasosInstancia");
        b.HasKey(x => x.Id);
        b.Property(x => x.Clase).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Nombre).HasMaxLength(100).IsRequired();
        b.Property(x => x.Comentario).HasMaxLength(2000);
        b.HasOne(x => x.Instancia).WithMany(i => i.Pasos).HasForeignKey(x => x.InstanciaFlujoId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.AsignadoA).WithMany().HasForeignKey(x => x.AsignadoAId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.DecididoPor).WithMany().HasForeignKey(x => x.DecididoPorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.InstanciaFlujoId, x.Orden }).IsUnique();
        b.HasIndex(x => x.AsignadoAId);
    }
}

internal sealed class NotificacionConfig : IEntityTypeConfiguration<Notificacion>
{
    public void Configure(EntityTypeBuilder<Notificacion> b)
    {
        b.ToTable("Notificaciones");
        b.HasKey(x => x.Id);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.Tipo).HasMaxLength(50).IsRequired();
        b.Property(x => x.Asunto).HasMaxLength(200).IsRequired();
        b.Property(x => x.Cuerpo).IsRequired();
        b.Property(x => x.CodigoDocumento).HasMaxLength(30);
        b.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.UltimoError).HasMaxLength(1000);
        b.Property(x => x.Clave).HasMaxLength(200);
        b.HasOne(x => x.Destinatario).WithMany().HasForeignKey(x => x.DestinatarioId).OnDelete(DeleteBehavior.Restrict);

        // La consulta del envío: «lo que está listo y ya le toca», en orden.
        b.HasIndex(x => new { x.Estado, x.ProximoIntentoUtc });

        // Idempotencia: una misma clave no puede encolarse dos veces (avisos periódicos).
        b.HasIndex(x => x.Clave).IsUnique().HasFilter("[Clave] IS NOT NULL");
    }
}

internal sealed class EventoAuditoriaConfig : IEntityTypeConfiguration<EventoAuditoria>
{
    public void Configure(EntityTypeBuilder<EventoAuditoria> b)
    {
        b.ToTable("Auditoria");
        b.HasKey(x => x.Id);
        b.Property(x => x.Usuario).HasMaxLength(256).IsRequired();
        b.Property(x => x.Accion).HasMaxLength(100).IsRequired();
        b.Property(x => x.Entidad).HasMaxLength(100).IsRequired();
        b.Property(x => x.EntidadId).HasMaxLength(100);
        b.Property(x => x.Comentario).HasMaxLength(2000);
        b.Property(x => x.DireccionIp).HasMaxLength(45);
        b.HasIndex(x => x.FechaUtc);
        b.HasIndex(x => new { x.Entidad, x.EntidadId });
    }
}
