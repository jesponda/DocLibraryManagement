using DocLibraryManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Persistencia;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Area> Areas => Set<Area>();
    public DbSet<SubArea> SubAreas => Set<SubArea>();
    public DbSet<TipoDocumento> TiposDocumento => Set<TipoDocumento>();
    public DbSet<AreaTipoDocumento> AreasTiposDocumento => Set<AreaTipoDocumento>();
    public DbSet<SubAreaTipoDocumento> SubAreasTiposDocumento => Set<SubAreaTipoDocumento>();
    public DbSet<Documento> Documentos => Set<Documento>();
    public DbSet<RevisionDocumento> Revisiones => Set<RevisionDocumento>();
    public DbSet<PasoPlantilla> PasosPlantilla => Set<PasoPlantilla>();
    public DbSet<InstanciaFlujo> InstanciasFlujo => Set<InstanciaFlujo>();
    public DbSet<PasoInstancia> PasosInstancia => Set<PasoInstancia>();
    public DbSet<Notificacion> Notificaciones => Set<Notificacion>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<EventoAuditoria> Auditoria => Set<EventoAuditoria>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ProtegerBitacora();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ProtegerBitacora();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// La bitácora es de solo inserción (7.6). Esta es la primera barrera; la segunda es que el
    /// usuario de BD de la aplicación no tenga UPDATE/DELETE sobre la tabla (ver docs de despliegue).
    /// </summary>
    private void ProtegerBitacora()
    {
        var violacion = ChangeTracker.Entries<EventoAuditoria>()
            .Any(e => e.State is EntityState.Modified or EntityState.Deleted);

        if (violacion)
        {
            throw new InvalidOperationException(
                "La bitácora de auditoría es de solo inserción: no se puede modificar ni eliminar.");
        }
    }
}
