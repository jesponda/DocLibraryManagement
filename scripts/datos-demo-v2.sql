/* ===========================================================================================================================
   DATOS DE DEMOSTRACIÓN (rama V2) · SOLO DESARROLLO / DEMOSTRACIÓN. NO EJECUTAR EN PRODUCCIÓN.

   Inserta documentos VIGENTES de ejemplo (los del prototipo de Calidad) para que el Home, la lista maestra y los reportes
   de vencimientos se vean con números. Son filas directas en la base: NO tienen archivo PDF (no se pueden abrir en el visor)
   y no pasaron por el flujo de aprobación. Para probar el ciclo completo, crea documentos desde la aplicación.

   Antes de ejecutarlo:
     1. Inicia la aplicación y carga el catálogo estándar desde Administración.
     2. Inicia sesión una vez como «autor-cal» (así existe el usuario responsable de los documentos).

   Uso (con -I: la base tiene índices filtrados y exige QUOTED_IDENTIFIER; con -f 65001 para conservar los acentos):
     sqlcmd -I -b -f 65001 -S "(localdb)\mssqllocaldb" -d "DocLibraryManagement-V2-Dev" -E -i scripts\datos-demo-v2.sql

   Es repetible: no duplica lo que ya existe (compara por código de documento).
   =========================================================================================================================== */
SET NOCOUNT ON;

DECLARE @autor uniqueidentifier = (SELECT TOP 1 Id FROM Usuarios WHERE Identificador = N'DEV\autor-cal');
IF @autor IS NULL
BEGIN
    RAISERROR(N'No existe el usuario DEV\autor-cal: inicia sesión una vez con esa persona y vuelve a ejecutar.', 16, 1);
    RETURN;
END

IF NOT EXISTS (SELECT 1 FROM Areas WHERE Codigo = N'QA')
BEGIN
    RAISERROR(N'No está cargado el catálogo estándar: cárgalo desde Administración y vuelve a ejecutar.', 16, 1);
    RETURN;
END

DECLARE @demo TABLE (tipo nvarchar(10), area nvarchar(10), unidad nvarchar(10), n int, titulo nvarchar(300), aprobadoHace int);

INSERT INTO @demo (tipo, area, unidad, n, titulo, aprobadoHace) VALUES
 (N'F',   N'QA',  N'II',  1, N'Inspección de recibo',                               200),
 (N'P',   N'QA',  N'II',  1, N'Procedimiento de inspección de material de entrada', 400),  -- ya venció (más de 12 meses)
 (N'PI',  N'QA',  N'II',  2, N'Plan de inspección coil acero',                      700),  -- vencido hace casi un año
 (N'HTE', N'QA',  N'MET', 1, N'Medición con calibrador digital',                     90),
 (N'P',   N'QA',  N'MET', 2, N'Control metrológico',                                310),  -- vence en ~55 días
 (N'AV',  N'QA',  N'DC',  1, N'Palanca de almacenamiento',                          120),
 (N'PI',  N'QA',  N'DC',  4, N'Plan de inspección palancas',                        340),  -- vence en ~25 días
 (N'HTE', N'QA',  N'DC',  5, N'Inspección hora por hora die cast',                   30),
 (N'F',   N'QA',  N'PL',  1, N'Etiquetas con defectos',                             210),
 (N'HTE', N'QA',  N'PL',  7, N'Hoja de trabajo platinado barril',                   355),  -- vence en ~10 días
 (N'AV',  N'QA',  N'SM',  3, N'Mal satinado',                                       250),
 (N'F',   N'QA',  N'SM',  1, N'Auditoría lijado y satinado manual',                  95),
 (N'P',   N'SGD', N'CD',  1, N'Control de documentos',                              180),
 (N'P',   N'SGD', N'CAP', 1, N'Acciones correctivas y preventivas',                 260),
 (N'P',   N'ENG', N'MNT', 1, N'Mantenimiento autónomo TPM',                          70),
 (N'HTE', N'PR',  N'DC',  1, N'Set up de máquina die cast',                          45),
 (N'P',   N'EHS', N'SGS', 1, N'Uso de equipo de protección personal',               130);

-- Documentos (el código sigue el estándar SITIO-TIPO-ÁREA-UNIDAD-NNN).
INSERT INTO Documentos (Codigo, Titulo, AreaId, SubAreaId, TipoDocumentoId, Consecutivo, Estado, ResponsableId, FechaCreacionUtc)
SELECT CONCAT(N'QRT-', d.tipo, N'-', d.area, N'-', d.unidad, N'-', RIGHT(CONCAT(N'00', d.n), 3)),
       d.titulo, a.Id, s.Id, t.Id, d.n, N'Activo', @autor, DATEADD(DAY, -d.aprobadoHace - 7, SYSUTCDATETIME())
FROM @demo d
JOIN Areas a ON a.Codigo = d.area
JOIN SubAreas s ON s.AreaId = a.Id AND s.Codigo = d.unidad
JOIN TiposDocumento t ON t.Codigo = d.tipo
WHERE NOT EXISTS (SELECT 1 FROM Documentos x
                  WHERE x.Codigo = CONCAT(N'QRT-', d.tipo, N'-', d.area, N'-', d.unidad, N'-', RIGHT(CONCAT(N'00', d.n), 3)));

-- Su revisión inicial, ya vigente. La próxima revisión es un año después de la aprobación (política: revisión anual).
INSERT INTO Revisiones (DocumentoId, Mayor, Menor, Estado, MotivoCambio, FechaCreacionUtc, FechaAprobacionUtc, FechaVigenciaUtc, FechaProximaRevisionUtc, AutorId)
SELECT doc.Id, 0, 0, N'Vigente', N'Emisión inicial (dato de demostración)',
       DATEADD(DAY, -d.aprobadoHace - 7, SYSUTCDATETIME()),
       DATEADD(DAY, -d.aprobadoHace, SYSUTCDATETIME()),
       DATEADD(DAY, -d.aprobadoHace, SYSUTCDATETIME()),
       DATEADD(MONTH, 12, DATEADD(DAY, -d.aprobadoHace, SYSUTCDATETIME())),
       @autor
FROM @demo d
JOIN Documentos doc ON doc.Codigo = CONCAT(N'QRT-', d.tipo, N'-', d.area, N'-', d.unidad, N'-', RIGHT(CONCAT(N'00', d.n), 3))
WHERE NOT EXISTS (SELECT 1 FROM Revisiones r WHERE r.DocumentoId = doc.Id);

SELECT COUNT(*) AS DocumentosDemostracion FROM Documentos WHERE Codigo LIKE N'QRT-%';
