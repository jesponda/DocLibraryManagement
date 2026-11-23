# Guía de validación y aceptación — V2

Para validar la rama `V2` antes de las pruebas con usuarios y la entrega. Marca cada punto; lo que falle se anota con
pantalla, usuario y hora.

## 1. Preparación (una vez)

| # | Paso | Resultado esperado |
|---|---|---|
| 1.1 | Base de datos **nueva y vacía** (o respaldo de la actual) con las migraciones aplicadas. | Sin errores al migrar. |
| 1.2 | `Autenticacion:Modo = Local`. Define `Autenticacion:AdministradorInicial` (usuario y contraseña temporal) o déjala vacía. | Al arrancar se crea el Administrador inicial. Si no definiste contraseña, aparece **una vez** en el registro (*Warning*, categoría `Cuentas`). |
| 1.3 | Configura `Archivos:RutaBase` (carpeta con respaldo) y `Documentos:CodigoSitio` (`QRT`). | La carpeta existe y la cuenta del servicio puede escribir. |
| 1.4 | Entra como el Administrador inicial. | Te obliga a cambiar la contraseña antes de seguir. |
| 1.5 | *Administración → Catálogo → Cargar catálogo estándar*. | 9 áreas, 66 subáreas, 20 tipos. Repetir la carga no duplica nada. |
| 1.6 | *Administración → Usuarios y roles*: da de alta una persona por rol (Autor con sus áreas, Revisor, Aprobador, Auditor, Lector). | Cada alta muestra su contraseña temporal una sola vez. |

## 2. Recorrido funcional (con personas distintas)

**Acceso**
- [ ] Contraseña errónea: mensaje genérico «Usuario o contraseña incorrectos» (igual para un usuario que no existe).
- [ ] Primer acceso con contraseña temporal: solo permite cambiarla; luego entra.
- [ ] 5 intentos fallidos bloquean 15 min; el Administrador la restablece y la persona entra.
- [ ] «Cerrar sesión» cierra; el botón Atrás no deja ver páginas internas.

**Autor** (área propia)
- [ ] *Nuevo documento*: área → subárea (unidad) → tipo; la vista previa muestra el código `QRT-TIPO-ÁREA-UNIDAD-NNN`.
- [ ] Crea el documento, sube el PDF (y fuente opcional) e inicia el flujo asignando revisor y aprobador.
- [ ] No puede crear ni editar en un área que no tiene asignada.

**Revisor y Aprobador**
- [ ] *Mis tareas* muestra la etapa pendiente (con contador en el menú).
- [ ] Rechazar exige comentario y regresa el documento al Autor; aprobar avanza.
- [ ] Tras la última aprobación, la revisión queda **Vigente** (o programada si se dio fecha futura).

**Todos**
- [ ] **Inicio**: tarjetas por área con subáreas y conteo de documentos; el buscador filtra por área y subárea; la nomenclatura de tipos lleva a la lista maestra.
- [ ] **Lista maestra**: un Lector ve solo lo vigente; filtros (texto, área, subárea, tipo); exporta a Excel y CSV con los mismos filtros.
- [ ] **Buscar**: encuentra por contenido del PDF y resalta; un Lector no ve obsoletos ni borradores.
- [ ] Enlace estable `…/doc/{código}` abre siempre la revisión vigente.

**Ciclo de vida**
- [ ] Nueva revisión mayor publicada: la anterior pasa a **Obsoleta** y desaparece para el Lector.
- [ ] Revisión menor: aprobación abreviada del dueño del área.
- [ ] Documento con revisión periódica vencida: se marca «Vencido» y aparece en *Reportes*.
- [ ] «Revisado sin cambios» extiende el vencimiento y queda en bitácora.

**Administrador / Auditor**
- [ ] Reportes (vencimientos y tareas por persona) y exportación.
- [ ] Bitácora: muestra altas de usuario, cambios de rol, inicios de sesión, bloqueos, aprobaciones. **Nunca** contiene contraseñas.
- [ ] Auditor: consulta todo, incluidos obsoletos, y no puede modificar nada.

## 3. Reglas que deben cumplirse (aceptación)

- [ ] El código es `SITIO-TIPO-ÁREA-UNIDAD-NNN`; el consecutivo es por tipo + área + unidad y **no se reutiliza**.
- [ ] La unidad (subárea) es obligatoria y **no se puede cambiar** después de creado el documento.
- [ ] Solo una revisión vigente por documento; los archivos publicados no se modifican (hash SHA-256).
- [ ] Desactivar a una persona la saca **en su siguiente clic**; cambiarle el rol también surte efecto sin que vuelva a entrar.
- [ ] Nadie puede quitarse el rol de Administrador ni desactivarse; no se puede dejar al sistema sin Administrador activo.
- [ ] Un Lector no puede abrir `/administracion`, `/auditoria`, `/documentos` ni un PDF no publicado escribiendo la dirección (acceso denegado).
- [ ] Sin sesión, cualquier dirección interna redirige al acceso; solo estilos, scripts y logo son públicos.

## 4. Pruebas con usuarios (sugerido)

- Grupo piloto de 5–8 personas: 1 Administrador, 2 Autores de áreas distintas, 1 Revisor, 1 Aprobador, 1 Auditor, 1–2 Lectores.
- Escenario guiado de 30 min: cada Autor crea un documento real, lo envía, se revisa y aprueba; un Lector lo encuentra en la lista maestra y por búsqueda.
- Recoge: ¿encontró lo que buscaba en el Inicio?, ¿entendió el código?, ¿cuántos clics tomó cada tarea?, ¿qué nombre o texto confundió?
- Prueba en los navegadores y pantallas que realmente usan (incluida la de planta, si aplica).

## 5. Criterios de aceptación para entregar

1. Todos los puntos de las secciones 1–3 en verde, sin errores abiertos de severidad alta.
2. Suite automatizada en verde (`dotnet test`, hoy 1058 pruebas).
3. Respaldo de la base y de la carpeta de archivos probado: se restaura en otro equipo y la lista maestra coincide.
4. El Administrador sabe: dar de alta, restablecer contraseña, desactivar, cargar catálogo y leer la bitácora.
5. Acta con fecha, versión (commit) y firma del responsable de Calidad.

## 6. Pendientes conocidos (no bloquean un piloto interno)

- Guía de operación y script de despliegue (Fase 5): aún no existen.
- Correo: probado solo con un SMTP de prueba; viene **desactivado**. Sin él no hay avisos de vencimiento (el estado «Vencido» sí se ve en pantalla).
- Modo Windows/AD: conservado pero **sin verificar** de extremo a extremo.
- Reglas de subáreas pendientes del negocio: autores por subárea y etiquetas de sensibilidad.
- Si se bloquea el único Administrador solo queda esperar 15 min o corregir la base a mano.
- En producción conservar el almacén de claves de *Data Protection*; si no, un reinicio cierra todas las sesiones.
