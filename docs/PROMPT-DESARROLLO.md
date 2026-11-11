# Prompt de desarrollo – Sistema de Gestión Documental (DocLibraryManagement)

| Campo | Valor |
|---|---|
| Versión del documento | 1.9 |
| Fecha | 2026-11-10 |
| Estado | Aprobado para iniciar Fase 1 |
| Norma base | ISO 9001:2015 cláusula 7.5 (Información documentada), estructura Annex SL |

> **Cómo usar este documento:** es el prompt de referencia para el desarrollo asistido. Se entrega por fases (sección 9). Al iniciar una sesión, indica la fase en curso y pide que se lea este archivo completo. Todo cambio de alcance se registra en la sección 12 (Historial) y sube la versión del documento.

---

## 1. Contexto y objetivo

El gerente de planta solicitó una propuesta de sistema de gestión documental para tener **control sobre los documentos de las distintas áreas**. IT exige arquitectura en **C# / .NET** con interfaz en **Blazor**.

El sistema debe cumplir lo que exige la cláusula 7.5 de ISO 9001:2015 sobre información documentada:

1. Identificación y descripción de cada documento (código, título, formato, autor).
2. Revisión y aprobación para su adecuación **antes** de emitirse.
3. Control de cambios e identificación del estado de revisión.
4. Disponibilidad de la versión vigente donde y cuando se necesita.
5. Control de documentos obsoletos para evitar su uso no intencionado.
6. Protección contra pérdida de integridad, uso indebido y acceso no autorizado.

La estructura es compatible con Annex SL, por lo que un mismo documento controlado podrá servir a ISO 9001, 14001 y 45001 en el futuro.

## 2. Alcance

**Dentro de v1:** documentos controlados (procedimientos, instructivos, políticas, formatos, planos en PDF), con su ciclo de vida completo, lista maestra, búsqueda de texto completo, notificaciones por correo y reportes de vencimiento.

**Fuera de v1 (no implementar ni diseñar en profundidad):**
- Gestión de **registros** (evidencia llenada) y retención por clasificación (ISO 15489 / 16175).
- **Documentos de origen externo** (normas, legislación, especificaciones de clientes).
- **Lectura confirmada** por el personal.
- **Copia controlada / no controlada** con marca de agua al imprimir.
- **Firma electrónica formal** y cumplimiento de 21 CFR Part 11.
- Conversión automática de Word/Excel a PDF.

Dejar los puntos de extensión razonables (interfaces, enums abiertos), sin construir lo que no está pedido.

## 3. Restricciones tecnológicas

- **.NET 10**, C#, `Nullable` y `ImplicitUsings` habilitados.
- **Blazor Web App** con render interactivo en servidor (Interactive Server). Público: menos de 100 usuarios, red interna.
- **EF Core 10 + SQL Server** para metadatos, flujo y auditoría.
- **Archivos en file share / NAS** on-premises, detrás de la interfaz `IFileStore`.
- **Interfaz en español.**
- **Escala de diseño:** menos de 100 usuarios y menos de 1,000 documentos. No sobreingenierizar (sin microservicios, colas externas ni motores de búsqueda dedicados).

### Punto de partida existente
Proyecto `DocLibraryManagement/` con plantilla Blazor Web App: páginas de cuenta de ASP.NET Identity (2FA, passkeys), EF Core + SQL Server y Bootstrap. Se reutiliza como proyecto `Web`. Las páginas de login local de Identity se **reemplazan** por SSO (sección 5); retirar lo que quede sin uso (por ejemplo `Counter`, `Weather`).

## 4. Arquitectura

Solución en capas con dependencias hacia adentro:

```
DocLibraryManagement.Domain          // entidades, enums, reglas de negocio, sin dependencias
DocLibraryManagement.Application     // casos de uso, interfaces (IFileStore, IEmailSender), validaciones
DocLibraryManagement.Infrastructure  // EF Core, FileStore en disco/UNC, SMTP, extracción de texto PDF, jobs
DocLibraryManagement                 // proyecto Web: Blazor, autenticación, autorización, componentes
DocLibraryManagement.Tests           // xUnit: dominio y aplicación; integración con SQL Server (LocalDB)
```

Los proyectos usan el prefijo `DocLibraryManagement` (el del proyecto web existente) para mantener un único espacio de nombres. Para el reloj se usa el `TimeProvider` del framework (en pruebas, `FakeTimeProvider`), no una interfaz propia.

Criterios:
- Las **reglas de negocio viven en Domain/Application**, no en componentes Blazor ni en el `DbContext`.
- Reloj inyectable (`IClock` / `TimeProvider`) para poder probar vencimientos y vigencias diferidas.
- Tareas en segundo plano con `BackgroundService` (vencimientos, vigencias diferidas, envío de correo con cola en BD).
- Configuración por `appsettings` y *user secrets*; **sin secretos en el repositorio**.

## 5. Identidad, roles y permisos

**Autenticación:** SSO corporativo con OpenID Connect (Entra ID) por defecto. Si resulta ser AD on-prem puro, usar Windows Authentication; la capa de autorización no debe cambiar. El usuario se identifica por su identificador corporativo y se sincroniza en el primer inicio de sesión.

**Roles** (mapeados a grupos de AD mediante configuración, no en código):

| Rol | Puede |
|---|---|
| Lector | Ver y descargar el PDF **vigente** de cualquier área; buscar; ver lista maestra |
| Autor | Lo anterior, más crear y editar borradores **de su área** |
| Revisor | Revisar documentos asignados (aprobar el paso o devolver con comentarios) |
| Aprobador | Aprobar o rechazar en los pasos de aprobación asignados |
| Auditor | Solo lectura de todo, incluidos obsoletos, retirados y bitácora |
| Administrador | Gestionar catálogos, flujos por tipo, usuarios y roles, y ver todo |

Reglas:
- **Lectura abierta** a lo vigente para todo empleado autenticado. **Edición restringida por área**: un Autor solo edita documentos de las áreas en las que está habilitado. El permiso se asigna a nivel de **área** (no de subárea), y abarca todas sus subáreas.
- **Separación de funciones:** quien elabora una revisión **no puede** aprobarla ni revisarla en el mismo flujo. Validar en servidor.
- Toda autorización se valida en el servidor (no basta con ocultar botones).

## 6. Modelo de dominio

Entidades principales (nombres orientativos, ajustar a convenciones del código):

- **Area** (código/abreviatura, nombre, activa).
- **SubArea** (área, nombre, activa). Pertenece a una sola área. La jerarquía es de **dos niveles fijos** (Área → Subárea).
- **TipoDocumento** (código, nombre, meses de revisión periódica, plantilla de flujo).
- **AreaTipoDocumento / SubAreaTipoDocumento** (asociación de qué tipos de documento se pueden crear en cada área o subárea).
- **Documento** (id, código completo, título, área, subárea opcional, tipo, consecutivo, estado global, responsable, fecha de creación).
- **RevisionDocumento** (documento, número mayor, número menor, estado, motivo del cambio, descripción del cambio, fecha de aprobación, fecha de vigencia, fecha de próxima revisión, archivo fuente, archivo PDF, hash SHA-256 de cada archivo, texto extraído para búsqueda).
- **PlantillaFlujo / PasoFlujo** (tipo de documento, orden, tipo de paso: Revisión o Aprobación, rol o usuarios habilitados, obligatorio).
- **InstanciaFlujo / DecisionPaso** (revisión, paso, usuario, decisión, comentario, fecha).
- **EventoAuditoria** (ver sección 7.6). Solo inserción.
- **Notificacion** (destinatario, asunto, cuerpo, estado, intentos, fecha). Funciona como cola de correo.

### 6.1 Codificación
Formato `TTT-AAA-NNN` más revisión: por ejemplo `PRO-CAL-012 Rev.03`.
- `TTT` = abreviatura de tipo, `AAA` = abreviatura de área, `NNN` = consecutivo por (tipo, área), asignado automáticamente y **sin reutilización**, aunque el documento se retire.
- El consecutivo debe ser seguro ante concurrencia (restricción única + reintento o secuencia por BD).
- El código de un documento no cambia con las revisiones.
- La **subárea no forma parte del código** ni del consecutivo; es una clasificación. Así reorganizar subáreas no obliga a recodificar documentos.

### 6.1.1 Catálogo configurable de áreas, subáreas y tipos
Administrable por el rol Administrador, sin cambios de código:
1. **Áreas y subáreas:** alta, edición y **desactivación**. No se eliminan si tienen documentos; una área o subárea desactivada no admite documentos nuevos, pero los existentes siguen consultables.
2. **Tipos de documento:** alta, edición y desactivación, con sus meses de revisión y plantilla de flujo (7.2, 7.4).
3. **Asociación tipo ↔ área/subárea:** define qué tipos de documento se pueden crear en cada una. Si una subárea no tiene asociaciones propias, **hereda las de su área**. Al crear un documento, el selector de tipo solo ofrece los asociados.
4. El **área de un documento no se puede cambiar** después de crearlo (forma parte del código). La **subárea sí**, con evento de bitácora.
5. Todo cambio de catálogo queda en la bitácora (7.6).
6. Alcance deliberadamente mínimo: el catálogo **no** incluye otros atributos (por ejemplo, plantillas de documento, palabras clave o jerarquías adicionales). Se agregarán sobre la marcha si hacen falta.

### 6.2 Estados de la revisión

```
Borrador → En revisión → En aprobación → Aprobado → Vigente → Obsoleto
                 ↘ (rechazo) → Borrador            
Documento: Retirado (baja sin sucesor)
```

- **Borrador:** editable por el Autor del área. Puede cancelarse (se descarta sin consumir número de revisión).
- **En revisión / En aprobación:** siguen los pasos de la plantilla de flujo. Un rechazo devuelve a Borrador con comentarios obligatorios.
- **Aprobado:** flujo completo, **con fecha de vigencia futura**: espera a que llegue. Sin fecha futura la revisión no se detiene aquí: pasa a Vigente en el acto al dar la última aprobación. Mientras espera, **el Administrador puede cambiar la fecha o anular la aprobación y devolverla a Borrador** (ver «Ajustes tras la validación»).
- **Cancelado:** borrador descartado antes de emitirse. No cuenta como revisión abierta ni consume número de revisión.
- **Vigente:** única revisión visible como vigente para lectura general.
- **Obsoleto:** reemplazada por una posterior.
- **Retirado:** el documento completo se da de baja sin sucesor (requiere aprobación). Su última revisión vigente pasa a Obsoleta.

## 7. Reglas de negocio

### 7.1 Versiones
1. **Rev.00** es la primera emisión. Cada revisión **mayor** publicada suma 1 (Rev.01, Rev.02…).
2. Una revisión **menor** (`Rev.03.1`) es para correcciones editoriales: ortografía, formato, datos de contacto. Requiere un solo aprobador (el dueño del área), motivo obligatorio y una casilla obligatoria "este cambio no altera requisitos, pasos ni responsabilidades". La menor se reinicia al publicar una mayor.
3. Los borradores **no consumen** número de revisión.
4. **Una sola revisión Vigente por documento**, garantizada por **índice único filtrado en BD**, además de la validación de aplicación.
5. **Una sola revisión abierta** (Borrador, En revisión, En aprobación o Aprobado) por documento a la vez.
6. La nueva revisión se crea a partir de la vigente. **La vigente sigue en uso** hasta que la nueva pasa a Vigente.
7. Toda revisión exige **motivo y descripción del cambio** (ISO 7.5.3, "identificar los cambios").
8. Los archivos de una revisión que ya salió de Borrador son **inmutables**. Se guarda el hash SHA-256 y se verifica al descargar. Corregir implica una nueva revisión.
9. Enlace estable `/doc/{codigo}`: siempre resuelve a la revisión vigente. Un enlace directo a una revisión específica es válido, pero si es obsoleta muestra el banner de la sección 7.3.

### 7.2 Flujo de aprobación configurable
- Cada **tipo de documento** tiene una plantilla de flujo: pasos ordenados, cada uno de tipo Revisión o Aprobación, con rol o usuarios habilitados.
- Una plantilla debe tener **al menos un paso de Aprobación**. Los cambios en una plantilla **no afectan** a flujos ya iniciados (se toma una copia al iniciar).
- Al iniciar el flujo se asignan los usuarios concretos de cada paso. Se notifica por correo.
- Reasignar un paso queda en bitácora.
- La revisión menor usa un flujo abreviado fijo (7.1.2), independiente de la plantilla del tipo.

### 7.3 Vigencia y obsolescencia
- **Vigencia diferida opcional:** al aprobar, el aprobador puede indicar fecha de vigencia futura. La revisión queda Aprobada y un job la pasa a Vigente en esa fecha. La anterior sigue vigente hasta entonces.
- Al volverse Vigente una revisión, la anterior pasa a **Obsoleto en la misma transacción**.
- Los obsoletos **salen de la lista maestra y de la búsqueda por defecto**. Solo Auditor, Administrador y el Autor/área dueña pueden consultarlos, con un **banner visible** "OBSOLETO – ver revisión vigente" y enlace a la vigente.
- Los obsoletos **nunca se borran físicamente**. Retención indefinida en v1.

### 7.4 Revisión periódica
- Cada tipo define `MesesRevision` (por defecto 12). Al hacerse Vigente se calcula la fecha de próxima revisión.
- Alertas por correo a **30 y 7 días** antes al responsable del documento.
- Si vence: el documento **sigue Vigente**, se marca **"Vencido"**, aparece en el reporte y se escala por correo al responsable y al jefe del área.
- Resoluciones: (a) **"Revisado sin cambios"**: extiende el vencimiento sin crear revisión, con comentario y aprobación según la regla del tipo, y queda en bitácora; (b) iniciar una **nueva revisión**.

### 7.5 Archivos
- Cada revisión tiene **fuente editable** (Word/Excel, opcional) y **PDF publicado** (obligatorio). El autor sube el PDF; no hay conversión automática.
- Validar extensión, tipo MIME y *firma* del archivo, y tamaño máximo configurable. Nunca confiar en el nombre original; guardar con nombre generado (por ejemplo GUID) fuera de la raíz web.
- Los lectores solo descargan/ven el PDF. La fuente editable solo la ven Autor del área, Administrador y Auditor.
- Al publicar, extraer el texto del PDF e indexarlo (7.7). Si el PDF no tiene texto (escaneado), avisar al autor.

### 7.6 Bitácora de auditoría
- Evento por cada acción relevante: crear, editar borrador, enviar a flujo, cada decisión, publicar, obsolescer, retirar, revisión sin cambios, reasignación, cambios de catálogos y roles, y descargas de obsoletos.
- Campos: usuario, fecha/hora (UTC), acción, entidad, valores relevantes antes/después, comentario, IP.
- **Inmutable:** solo inserción. Sin pantallas de edición ni borrado. El usuario de BD de la aplicación no debe tener `UPDATE`/`DELETE` sobre esta tabla (documentarlo y reflejarlo en el script de despliegue).
- Visible para Auditor y Administrador, con filtros y exportación.

### 7.7 Búsqueda de texto completo
- Búsqueda por código, título, área, tipo, estado y **contenido** del PDF vigente.
- Implementación: texto extraído al publicar, guardado en una tabla/columna indexada con **SQL Server Full-Text Search**. Por defecto solo busca en revisiones Vigentes.
- Resultados paginados, con fragmento resaltado.

### 7.8 Notificaciones
- Por SMTP, mediante cola en BD con reintentos y registro de error. Sin enviar correo desde el hilo de la petición.
- Eventos: tarea de revisión o aprobación asignada, rechazo, publicación, vigencia diferida próxima, alerta de vencimiento (30 y 7 días), vencido y escalamiento.
- Plantillas en español, editables por configuración.

## 8. Requisitos no funcionales

- **Seguridad:** HTTPS; protección anti-CSRF; validación de entrada en servidor; sin secretos en el repositorio; cabeceras de seguridad razonables; revisión de las OWASP Top 10 relevantes (control de acceso, carga de archivos, registro).
- **Integridad:** transacciones en cambios de estado críticos (publicar + obsolescer); restricciones únicas e índices en BD que respalden las reglas.
- **Rendimiento:** lista maestra y búsqueda paginadas; respuesta de pantallas habituales menor a 2 s con la escala de diseño.
- **Operación:** logging estructurado; endpoint de salud; migraciones EF versionadas; script de despliegue que documente permisos de BD y del file share.
- **Respaldo:** documentar que BD y NAS deben respaldarse de forma coordinada (pendiente de política de IT).
- **Accesibilidad y UX:** interfaz clara para operadores de piso (pocos clics para llegar al PDF vigente), responsive básico, mensajes en español.
- **Calidad:** nullable respetado, sin advertencias nuevas, análisis estático habilitado.

## 9. Plan por fases

Cada fase debe terminar con: código compilando, pruebas pasando, resumen de lo hecho y lo pendiente, y **una pausa para revisión del usuario antes de iniciar la siguiente**.

### Fase 1 – Fundamentos
- Reestructurar en solución por capas (sección 4) conservando el proyecto actual como `Web`.
- Modelo de datos base (Area, TipoDocumento, Documento, RevisionDocumento, EventoAuditoria) y primera migración.
- SSO, roles mapeados a grupos y políticas de autorización; retirar el login local de Identity y las páginas de ejemplo.
- Layout, navegación y manejo de errores.
- **Aceptación:** un usuario inicia sesión por SSO, se le asigna rol por grupo, y las políticas de autorización tienen pruebas.

### Fase 2 – Ciclo de vida del documento
- Catálogos configurables (áreas, subáreas, tipos, asociación tipo ↔ área/subárea según 6.1.1, y plantillas de flujo) con pantallas para el Administrador.
- Creación de documentos con codificación automática (6.1).
- Borradores, carga de archivos (7.5), motor de flujo configurable (7.2), decisiones, rechazos.
- Publicación, revisión mayor y menor, vigencia diferida, obsolescencia automática, retiro (7.1, 7.3).
- Bitácora (7.6).
- **Aceptación:** crear → revisar → aprobar → publicar → crear nueva revisión → publicar → la anterior queda Obsoleta; separación de funciones y unicidad de vigente probadas, incluida una prueba de concurrencia.

**Entrega en tres tramos**, con pausa de revisión al final de cada uno:
- **2A – Catálogos y creación de documentos** *(entregado)*: catálogos configurables, creación con código automático (primera revisión Rev.00 en Borrador), listado y detalle básicos, copia de roles del usuario.
- **2B – Borradores, archivos y flujo** *(entregado y validado)*: carga de archivos (7.5), plantillas de flujo por tipo (7.2), asignación de personas, decisiones, rechazos, reasignación y cancelación del flujo.
- **2C – Publicación y ciclo de vida** *(entregado y validado)*: publicación, revisión mayor y menor, vigencia diferida, obsolescencia automática y retiro (7.1, 7.3). La lógica de la activación diferida está aquí; ver la nota sobre su proceso de fondo en las decisiones del tramo.

**Decisiones de diseño de la Fase 2:**
- **Asignación de pasos a personas concretas:** al iniciar sesión se guarda en `Usuario` una **copia informativa** de sus roles y áreas (`RolesUltimoAcceso`, `AreasUltimoAcceso`). Con ella el autor elige candidatos al armar el flujo. Limitaciones: solo aparecen quienes ya entraron al menos una vez, y la copia puede estar desactualizada hasta 10 minutos. **Nunca se usa para autorizar:** la autorización siempre sale de los grupos de AD vigentes, y el servicio debe volver a validar que la persona asignada tenga el rol al decidir.
- **Servicios reciben un `Actor`** (identificador, roles, áreas, IP) y validan permisos por sí mismos; no dependen del `HttpContext`.
- **`IDbContextFactory`** en lugar de un `DbContext` por circuito (Blazor Server). Cada operación crea y libera el suyo.
- **Consecutivo de documento:** se calcula como máximo + 1 y se confía en el índice único; ante colisión, reintenta (hasta 15 veces con espera aleatoria). Los documentos nunca se borran, por lo que un número no se reutiliza.
- **La IP en la bitácora es de mejor esfuerzo:** en Blazor Server el `HttpContext` solo existe en la solicitud inicial del circuito.

**Decisiones del tramo 2B:**
- **Plantilla de flujo:** lista ordenada de etapas de tipo *Revisión* (rol Revisor) o *Aprobación* (rol Aprobador), máximo 10. **La última etapa debe ser una aprobación.** Al enviar a revisión se *copia* la plantilla; cambiarla después no afecta flujos en curso. Cada envío es una instancia numerada (#1, #2…); un rechazo o cancelación cierra la instancia y un reenvío abre otra, conservando el historial.
- **Etapas secuenciales.** El estado de la revisión sigue a la etapa vigente (*En revisión* / *En aprobación*); al aprobar la última pasa a *Aprobado*. Esa transición a *Vigente* es del tramo 2C.
- **Separación de funciones:** el autor de la revisión (`AutorId`) no puede ser asignado ni decidir en su flujo; se valida al iniciar, al reasignar y al decidir.
- **Revalidación al decidir:** el rol se comprueba con los roles *vivos* del Actor (grupos de AD), no con la copia guardada; además solo decide la persona asignada, en su turno.
- **Rechazo:** exige comentario, devuelve la revisión a *Borrador* y marca *Omitidas* las etapas restantes. El comentario se muestra al autor.
- **Reasignar / cancelar flujo:** las hace quien edita en el área (o el Administrador), con motivo obligatorio y bitácora. Cancelar devuelve la revisión a *Borrador*.
- **Cancelar un documento nuevo** (primera revisión aún en borrador): la revisión pasa al nuevo estado **Cancelado** y el documento a **Retirado**; el consecutivo nunca se reutiliza. Para revisiones posteriores a la primera, el descarte de borradores se define en el tramo 2C.
- **Archivos:** PDF obligatorio para enviar a revisión; fuente editable opcional. Se valida la **extensión y la firma real** del contenido (un ejecutable renombrado a `.pdf` se rechaza), se guarda con nombre generado (GUID) en `año/mes/` fuera de `wwwroot`, con SHA-256, escritura atómica (temporal + mover) y límite de tamaño configurable (`Archivos:TamanoMaximoMb`, 50 por defecto). Reemplazar el archivo de un borrador borra el anterior. Los archivos de una revisión que salió de *Borrador* son inmutables.
- **Descarga** por `GET /archivos/{revisionId}/{pdf|fuente}`: exige sesión, revalida permisos y **verifica el hash en cada descarga**; si no coincide (o el archivo falta) no lo entrega y deja evento en la bitácora. El PDF lo ven gestores (Administrador, Auditor, Autores del área) y quienes tengan una etapa asignada; la **fuente solo los gestores**; lo *Vigente* es de lectura abierta. Descargar una revisión obsoleta queda registrado.
- **Concurrencia:** `RowVersion` en la revisión (una edición y un cambio de flujo simultáneos no se pisan) e índice único filtrado `UX_Flujo_UnicoEnCurso` (un solo flujo en curso por revisión).
- **Visibilidad:** quien tiene (o tuvo) una etapa asignada en un documento puede ver el documento, su flujo y su PDF aunque no sea gestor.
- **Pendiente de producción:** definir la ruta real del almacén (`Archivos:RutaBase`, local o de red) y sus permisos y respaldo; hoy, vacía, usa `App_Data/archivos` junto a la aplicación.

**Decisiones del tramo 2C:**
- **Publicación automática:** al aprobar la **última** etapa del flujo la revisión pasa a *Vigente* en el acto y la anterior a *Obsoleto*, en una misma transacción. Esa transición tiene una sola puerta (`Publicacion.PublicarAsync`) y la garantiza además el índice único «una sola vigente por documento». Como el índice no tolera dos vigentes ni a mitad de una sentencia, el guardado se hace en dos pasos dentro de la transacción (primero se libera la anterior).
- **Vigencia diferida:** quien da la última aprobación puede indicar una fecha (máximo 365 días). Con fecha futura la revisión queda *Aprobado*, la vigente sigue en uso y, al llegar la fecha, se publica con la fecha **programada** (no la hora real en que corrió el proceso). Sin fecha, o con una ya pasada, es inmediata. La fecha se ignora en etapas intermedias y al rechazar.
- **Próxima revisión periódica:** al hacerse *Vigente* se calcula como la vigencia + los meses del tipo (`MesesRevision`). El aviso y la gestión de vencimientos son de la Fase 4.
- **Activación diferida:** la lógica es `IPublicacionService.ActivarPendientesAsync` (idempotente; cada revisión en su transacción, un fallo no afecta a las demás). Se dispara con un proceso de fondo mínimo (`TareasDeFondo`, ver Fase 3), configurable con `TareasDeFondo:MinutosEntreEjecuciones` (5 por defecto; 0 lo desactiva). **Se adelantó desde la Fase 4** porque sin él la vigencia diferida no funcionaría de punta a punta; el resto de trabajos programados (vencimientos, alertas, correo) siguen en la Fase 4.
- **Numeración:** `Rev.00` es la primera emisión. Una **mayor** suma 1 y reinicia la menor (Rev.03 → Rev.04); una **menor** suma 1 a la menor (Rev.03 → Rev.03.1 → Rev.03.2). Un **borrador cancelado no consume número**: el índice único (documento, mayor, menor) excluye las revisiones *Cancelado*, de modo que el siguiente borrador puede volver a ser Rev.01.
- **Una sola revisión abierta** por documento: no se puede abrir otra mientras haya un borrador, un flujo en curso o una revisión aprobada pendiente de entrar en vigor. Se abre siempre a partir de la vigente; los archivos no se heredan (se sube el PDF nuevo).
- **Revisión menor:** exige declarar que *no altera requisitos, pasos ni responsabilidades* (queda en la bitácora). Se aprueba en **una sola etapa** y **no usa la plantilla del tipo**. El aprobador es el **dueño del área**, una figura nueva: el Administrador designa por área a una persona con rol Aprobador (`Area.DuenoId`). Sin dueño no se pueden abrir ni enviar menores. El dueño no puede aprobar una menor que él mismo elaboró (separación de funciones) y debe seguir teniendo el rol Aprobador.
- **Descartar borradores:** un borrador posterior a la primera emisión se descarta quedando *Cancelado* (la primera emisión se cancela con «Cancelar este documento», que retira el código).
- **Retiro:** solo el **Administrador**, con motivo obligatorio y bitácora (decisión de la v1; no hay flujo de solicitud y aprobación). La revisión vigente pasa a *Obsoleto* y todo lo abierto (borradores, flujos en curso, revisión aprobada pendiente) se cancela, sin dejar tareas pendientes huérfanas. El código no se reutiliza.
- **Obsoletos:** conservan sus archivos intactos (se sigue verificando el hash) y solo los consultan gestores; descargar una obsoleta queda en la bitácora. La lista maestra y el banner de obsoleto para lectura se hicieron en la Fase 3.

### Fase 3 – Consulta *(entregada y validada)*
- Lista maestra (filtros, exportación a Excel/CSV).
- Visor del PDF vigente, enlace estable `/doc/{codigo}`, vista de obsoletos con banner.
- Búsqueda de texto completo (7.7).
- **Aceptación:** un Lector llega al PDF vigente en pocos clics; una búsqueda por contenido encuentra el documento correcto; un Lector no ve obsoletos.

**Decisiones de la Fase 3:**
- **Búsqueda propia en lugar de SQL Server Full-Text.** El LocalDB de desarrollo **no incluye Full-Text Search** (`IsFullTextInstalled = 0`), así que el prompt original (7.7) no era ejecutable ni probable allí. Se decidió una búsqueda con SQL estándar, **igual en desarrollo y producción**, adecuada para <1,000 documentos: cada palabra se busca con `LIKE` sobre código, título y texto extraído usando la intercalación explícita `Latin1_General_100_CI_AI` (sin distinguir mayúsculas ni acentos, sea cual sea la de la BD). Todas las palabras deben aparecer (AND). **Limitaciones aceptadas:** no hace raíces de palabras (buscar «calibrar» no encuentra «calibración») ni una relevancia fina; la relevancia es en dos niveles (primero lo que coincide en código o título, luego solo en el contenido, y dentro de cada nivel por código). Si el volumen crece, la consulta está detrás de `IConsultaService` y puede cambiarse a Full-Text en un servidor que lo tenga sin tocar las pantallas.
- **Lectura del texto de los PDF** con PdfPig (Apache-2.0, solo .NET, sin instalar nada en el servidor), tope de 2 millones de caracteres por PDF. Se lee **al subir el PDF** (no al publicar) para poder avisar al autor de inmediato; reemplazar el PDF reemplaza el texto. `TextoExtraido`: *null* = nunca leído, *vacío* = leído pero sin texto. Un PDF **escaneado o ilegible se acepta** igual, con un aviso al autor de que no se encontrará por contenido (sin OCR; bloquearlo impediría publicar documentos escaneados legítimos).
- **Indexado de lo ya publicado:** `IIndexadorService` lee el texto de revisiones vigentes/obsoletas/aprobadas que aún no lo tienen (p. ej. publicadas en la Fase 2), en lotes de 25, y no reintenta los PDF sin texto. Si el archivo falta, lo deja pendiente sin fallar.
- **`TareasDeFondo`** (antes `ActivadorDeVigencias`) agrupa las tareas periódicas: activar vigencias diferidas e indexar texto. Configuración `TareasDeFondo:MinutosEntreEjecuciones` (5; 0 desactiva). *El nombre de la opción anterior (`Publicacion:MinutosEntreRevisiones`) ya no existe.*
- **Lista maestra:** la revisión *vigente* de cada documento *activo*, lectura abierta; filtros por código/título, área, tipo y subárea; paginada (25) y ordenada por código. **Exportación a CSV** (UTF-8 con BOM, comas, con protección contra inyección de fórmulas: las celdas que empiezan con `= + - @` llevan apóstrofo) **y Excel** (ClosedXML, MIT; texto siempre literal, fechas como fechas), con los mismos filtros, tope de 5,000 filas, fechas en la hora local del servidor.
- **Búsqueda (`/buscar`):** por defecto solo revisiones vigentes de documentos activos. **Incluir obsoletas** (consulta de auditoría) solo lo pueden pedir Administrador y Auditor; el servicio lo exige aunque la URL lo pida. Resultados paginados (20) con fragmento resaltado; el resaltado se devuelve como segmentos de texto, nunca como HTML, así que el contenido del PDF no puede inyectar marcado.
- **Visor y enlace estable:** `/doc/{codigo}` lleva siempre a la revisión vigente (el código no distingue mayúsculas) con el PDF incrustado. `/doc/{codigo}/{etiqueta}` (p. ej. `Rev.00`) muestra una revisión concreta: una obsoleta solo la ven Administrador, Auditor y Autores del área, con un banner rojo **OBSOLETO – No debe usarse** y enlace a la vigente. Un documento inexistente, no publicado o retirado muestra **el mismo mensaje genérico** a un lector, sin revelar su existencia ni su título.
- **No incluido (pendiente):** los **vencimientos y alertas** de la revisión periódica siguen en la Fase 4; el **texto de los PDF no se vuelve a leer** si cambia el motor de extracción (habría que invalidar `TextoExtraido`).

**Ajustes tras la validación (2026-11-09) — aprobación pendiente de entrar en vigor:**
Hallazgo del usuario: una revisión aprobada con vigencia futura (estado *Aprobado*) no tenía salida salvo esperar la fecha o
retirar el documento completo. Se agregó, **solo para el Administrador y con motivo obligatorio y bitácora**:
- **Cambiar la fecha de vigencia** (`ReprogramarVigenciaAsync`): la nueva fecha debe ser **futura** y de hasta 365 días; la aprobación se
  conserva y la revisión se publicará con la fecha nueva. Una fecha pasada o actual se rechaza (equivaldría a «publicar ya», que **no** se
  incluyó por decisión).
- **Devolver a borrador** (`RevocarAprobacionAsync`): anula la aprobación y la revisión vuelve a *Borrador*; el autor corrige y debe
  **reenviarla por el flujo completo** (envío nuevo). El envío anulado queda en el historial con el nuevo estado de flujo **`Revocado`**
  (sus etapas conservan su decisión) y `InstanciaFlujo.MotivoCierre` guarda el motivo, que el autor ve en el panel. La vigente actual
  nunca se afecta. Al anular desaparecen las fechas de aprobación y vigencia.
- **Alcance:** solo revisiones en estado *Aprobado* (aún no vigentes). **No se permite revertir una revisión que ya es vigente** (volver a
  poner la anterior): reactivar una obsoleta rompe la garantía de 7.5.3 de que lo obsoleto no vuelve a usarse; la salida para lo ya
  vigente sigue siendo abrir una revisión nueva o retirar el documento. Tampoco se incluyeron «publicar ahora» ni «descartarla».
- **Carrera con `TareasDeFondo`:** si el proceso publica la revisión antes de que el Administrador actúe, la acción se rechaza con un
  mensaje claro (la revisión ya está vigente). Mientras el proceso no corra, anular o reprogramar sigue siendo posible aunque la fecha ya
  haya pasado.
- **Cambio relacionado:** el motivo por el que una revisión volvió a *Borrador* se calcula ahora **solo del último envío** (antes se
  tomaba cualquier rechazo anterior, y podía mostrarse un motivo viejo que ya no aplicaba).
- Migración `MotivoCierreFlujo` (una columna nula).

### Fase 4 – Alertas y reportes
Se entrega en tres tramos: **4A** correo, **4B** vencimientos, **4C** reportes.
- Jobs de vigencia diferida y vencimientos (7.4), escalamiento.
- Cola de correo y plantillas (7.8).
- Reportes: documentos vencidos/por vencer, tareas pendientes por usuario, historial de revisiones por documento.
- **Aceptación:** con reloj simulado, se generan las alertas a 30 y 7 días, el estado "Vencido" y el escalamiento.

#### Tramo 4A – Correo *(construido y validado)*
Decisiones:
- **Cola en BD** (`Notificaciones`): estados Pendiente / Procesando / Enviado / Error, con reintentos de espera exponencial
  (`MinutosReintento` × 2^(n−1), hasta `MaximoIntentos`). El aviso se encola **en la misma transacción** que el cambio de negocio
  (si la operación falla no queda correo; si se confirma, el correo no se pierde). Un `BackgroundService` independiente envía.
- **Entrega al-menos-una-vez:** se reserva con `RowVersion` y un arriendo (`MinutosArriendo`); si un proceso muere a mitad del
  envío, otro la retoma al vencer el arriendo. Un correo podría duplicarse en ese caso extremo; nunca perderse.
- **Tres modos** (`Correo:Modo`): `Desactivado` (predeterminado: no encola nada), `Carpeta` (archivos `.eml`, para desarrollo)
  y `Smtp` (MailKit). 
- **Dirección del destinatario:** correo capturado por el Administrador > convención configurable `PatronCorreo`
  (p. ej. `{usuario}@empresa.com`) > sin correo. Se resuelve **al enviar**, no al encolar, para que corregir una dirección
  arregle también los reintentos. Pantalla `/administracion/usuarios` para capturarlo.
- **Plantillas en español** con reemplazo por configuración (`Correo:Plantillas:<Evento>:Asunto|Cuerpo`); un marcador
  desconocido queda visible en el correo. El asunto se normaliza a una línea (sin inyección de cabeceras) y se recorta a 200.
- **Eventos de este tramo:** tarea asignada (al iniciar, al aprobar una etapa —avisa a la siguiente—, al reasignar), rechazo
  (al autor), publicación (al autor y al responsable) , aprobación anulada y vigencia reprogramada. **Quien ejecuta la acción
  no recibe aviso de su propia acción.** Personas desactivadas no reciben avisos.
- Pantalla `/administracion/correo` (solo Administrador): cola con filtro, último error y *Reintentar* para las en Error
  (queda en la bitácora). Avisa claramente cuando el modo es `Desactivado`.

#### Tramo 4B – Vencimientos *(construido, pendiente de validar)*
Decisiones:
- **«Vencido» es un estado derivado** de `FechaProximaRevisionUtc` (no se guarda): el documento sigue Vigente y se marca en
  pantalla (panel de la revisión, insignia en lista maestra y visor).
- **Avisos** (proceso de fondo `TareasDeFondo`): a 30 y a 7 días al responsable (límites inclusivos; si la primera vez que se
  nota faltan <7 días, solo sale el urgente); al vencer, al responsable y **escalamiento al dueño del área** (si no hay, está
  inactivo o es el mismo responsable → a los Administradores); y **vigencia próxima** (3 días antes) al autor y al responsable.
- **Idempotencia** por `Notificacion.Clave` = tipo + revisión + marca de la fecha + destinatario: no se duplica al repetir
  el proceso y, al extenderse la fecha, el ciclo siguiente vuelve a avisar.
- **«Revisado sin cambios»:** lo propone el responsable (o un Administrador) con comentario obligatorio, **solo desde 30 días
  antes** del vencimiento o ya vencido; lo decide el **dueño del área** (o un Administrador) **en una sola etapa**; quien propone
  nunca decide. Aprobar extiende **un periodo completo desde la fecha vigente** (si aún no vence) o **desde hoy** (si ya venció);
  rechazar exige motivo y no cambia la fecha. Una sola pendiente por revisión (índice único filtrado); se cancela sola si la
  revisión deja de ser la vigente. Bitácora `Revision.SinCambios.*`.
- Nueva tabla `SolicitudesSinCambios` (migración `SolicitudesSinCambios`).
- **Acceso del dueño del área** (hallado en la validación manual): el dueño (con rol Aprobador vigente) puede ver, solo lectura,
  los documentos de su área aunque nunca haya tenido una etapa; sin eso no podía abrir el documento para decidir. No se abre el
  módulo Documentos a Revisor/Aprobador (listaría borradores ajenos); el seguimiento del área irá en el reporte de vencimientos de 4C.

#### Tramo 4C – Reportes *(pendiente)*
Vencidos y por vencer, tareas pendientes por usuario, historial de revisiones por documento, **pantalla de consulta de la
bitácora** (Auditor/Administrador, con filtros y exportación) y **cambio de responsable** de un documento por el Administrador
(motivo obligatorio y bitácora).

### Fase 5 – Pruebas y endurecimiento
- Revisión de seguridad, pruebas de integración, datos de ejemplo, script de despliegue y guía de operación.
- **Aceptación:** checklist de la sección 11 completo.

## 10. Datos pendientes y supuestos vigentes

Supuestos tomados por defecto (cambiarlos requiere actualizar este documento):
- Entra ID / OpenID Connect; Windows Auth como alternativa.
- El autor sube el PDF; sin conversión automática.
- Texto del PDF extraído al publicar e indexado en SQL Server.
- Roles de la sección 5, mapeados a grupos de AD.
- Interfaz en español.

Estado de la autenticación tras la Fase 1:
- **Modo `Windows` (Negotiate):** el servidor emite el desafío correctamente, pero **no se pudo completar el apretón de manos de extremo a extremo** en el equipo de desarrollo (no unido a un dominio clásico; el handler registra `InvalidToken`). Debe verificarse en un servidor unido al dominio, con los grupos reales de AD. Hasta entonces, la resolución grupo → rol está cubierta por pruebas unitarias, pero **no probada contra AD real**.
- **Modo `Desarrollo`:** usuarios simulados (`appsettings.Development.json`), solo disponible en el entorno Development; el arranque falla si se activa en otro.
- Los grupos de AD se resuelven y se cachean 10 minutos por usuario (`Autorizacion:MinutosCache`): un cambio de grupos puede tardar ese tiempo en reflejarse.

Pendientes de datos reales (no bloquean la Fase 1, bloquean la puesta en producción):
- Carga inicial del catálogo (áreas, subáreas, tipos y abreviaturas). Ya no bloquea el desarrollo: el catálogo es configurable (6.1.1) y se puede ir completando sobre la marcha.
- **Servidor SMTP** y remitente.
- **Política de respaldo** de BD y NAS.
- Tenant/dominio y **grupos de AD** para el mapeo de roles.
- Criterios de **"revisión menor"** validados por el área de Calidad.

## 11. Definición de "terminado" para v1

- [ ] Reglas 7.1 a 7.8 cubiertas por pruebas automatizadas.
- [ ] Un solo documento Vigente por código, verificado también a nivel de BD.
- [ ] Ningún usuario puede aprobar su propia revisión.
- [ ] La bitácora no puede modificarse desde la aplicación.
- [ ] Un Lector no puede editar ni ver obsoletos ni la fuente editable.
- [ ] Recorrido completo probado manualmente en la aplicación en ejecución.
- [ ] Sin secretos en el repositorio; migraciones aplican sobre BD vacía.
- [ ] Guía de operación y despliegue entregada.

## 12. Acuerdos de trabajo para el asistente

1. **No ampliar el alcance** de la sección 2. Si algo parece necesario y no está aquí, preguntar antes.
2. **Preguntar ante ambigüedad** de reglas de negocio; no decidir en silencio.
3. Cambios pequeños y verificables. Compilar y ejecutar las pruebas antes de dar una tarea por terminada, e informar con claridad lo que falló.
4. Mantener las reglas de negocio en Domain/Application, con pruebas.
5. No tocar la configuración de seguridad, permisos ni datos existentes sin avisar.
6. Al terminar cada fase, resumir y esperar confirmación.

## 13. Historial del documento

| Versión | Fecha | Cambio |
|---|---|---|
| 1.0 | 2026-11-05 | Versión inicial: requerimientos levantados con el usuario (norma base, alcance, flujo, identidad, versiones y obsoletos). |
| 1.1 | 2026-11-05 | Catálogo configurable Área → Subárea con tipos de documento asociados (6.1.1); el resto de atributos se define sobre la marcha. |
| 1.2 | 2026-11-05 | Fase 1 construida. Nombres de proyectos alineados con el código (sección 4). Se documenta el modo de autenticación de desarrollo y la verificación pendiente de Windows Auth (sección 10). |
| 1.3 | 2026-11-05 | Tramo 2A construido (catálogos y creación de documentos). Se documentan los tramos de la Fase 2 y sus decisiones de diseño. |
| 1.4 | 2026-11-05 | Tramo 2B construido (archivos, plantillas de flujo, decisiones). Nuevo estado de revisión *Cancelado*. Decisiones del tramo documentadas. |
| 1.5 | 2026-11-05 | Tramo 2C construido (publicación automática, vigencia diferida, revisiones mayor/menor, dueño de área, descarte de borradores, retiro). Se adelanta un proceso de fondo mínimo para la activación diferida. |
| 1.6 | 2026-11-09 | Fase 3 construida (lista maestra con exportación, búsqueda por contenido, visor y enlace estable). La búsqueda no usa SQL Server Full-Text porque el LocalDB de desarrollo no lo incluye. `ActivadorDeVigencias` pasa a `TareasDeFondo`. |
| 1.7 | 2026-11-09 | Fase 3 validada. Ajuste: el Administrador puede cambiar la fecha o anular (devolver a borrador) una aprobación que aún no entra en vigor. Nuevo estado de flujo `Revocado`. |
| 1.8 | 2026-11-10 | Fase 4 dividida en tramos 4A/4B/4C. Tramo 4A construido: cola de correo en BD con reintentos, plantillas, modos Desactivado/Carpeta/Smtp, avisos de los eventos existentes y pantallas de usuarios y de cola. |
| 1.9 | 2026-11-10 | Tramo 4A validado. Tramo 4B construido: avisos a 30/7 días, «Vencido» derivado, escalamiento al dueño del área, «revisado sin cambios» (propone el responsable, decide el dueño en una etapa) y aviso de vigencia próxima. |

## 14. Referencias

- ISO 9001:2015, cláusula 7.5 Información documentada (7.5.1, 7.5.2, 7.5.3). Verificar la redacción contra el texto oficial.
- ISO 15489-1:2016 e ISO 16175: marco de gestión de registros, fuera del alcance de v1 y referencia para evolución futura.
- MoReq2010 (DLM Forum): referencia para evolución futura.
