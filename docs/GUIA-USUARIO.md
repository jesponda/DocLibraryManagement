# SGD — Sistema de gestión documental · Guía rápida de uso

**Dirección:** `http://________:8001` (reemplaza por la IP o nombre que te indique el Administrador)
**Funciona con:** Edge, Chrome o Firefox actualizados, desde cualquier equipo de la red de la planta.

## 1. Primer acceso

1. Abre la dirección. Escribe tu **usuario** y la **contraseña temporal** que te dio el Administrador.
2. El sistema te pide una **contraseña propia**: mínimo 10 caracteres, distinta de la temporal y sin tu nombre de usuario.
3. Listo. Para cambiarla después: botón **Contraseña** (arriba a la derecha). Para salir: **Cerrar sesión**.

> Después de **5 intentos fallidos** la cuenta se bloquea 15 minutos. Si olvidaste tu contraseña, pide al Administrador que la restablezca.
> Nunca compartas tu usuario: lo que apruebes o modifiques queda registrado a tu nombre.

## 2. Qué verás según tu rol

| Rol | Qué puede hacer |
|---|---|
| **Lector** (todas las personas) | Consultar la **lista maestra**, buscar y abrir documentos **vigentes**. |
| **Autor** | Crear y editar documentos en **sus áreas**, subir el PDF y enviarlo a revisión. |
| **Revisor / Aprobador** | Revisar o aprobar los documentos que le asignan (**Mis tareas**). |
| **Auditor** | Consultar todo, incluidos obsoletos y la **bitácora**; no modifica nada. |
| **Administrador** | Todo lo anterior, más usuarios, catálogos y flujos. |

Tu rol y tus áreas están en **Mi acceso**. Si necesitas otro, pídelo al Administrador.

## 3. Consultar documentos (todas las personas)

- **Inicio:** tarjetas por área con sus subáreas y cuántos documentos vigentes tiene cada una. Pulsa el nombre del área o de una subárea para ver sus documentos.
- **Buscador del Inicio:** filtra por texto, área y subárea. «Buscar» filtra la lista maestra; también puedes buscar **dentro del contenido** de los PDF desde *Buscar*.
- **Lista maestra:** todos los documentos vigentes con su código, revisión y fecha. Se puede **exportar a Excel o CSV** con los filtros aplicados.
- **Solo ves lo vigente.** Un documento en trámite o una revisión anterior no aparece para los Lectores: es normal.

### ¿Cómo se lee un código?
`QRT-F-QA-II-001` → **Sitio** · **Tipo** (F = Formato) · **Área** (QA) · **Unidad/subárea** (II) · **Consecutivo**.
La revisión (`Rev.00`, `Rev.01`…) **no** es parte del código. El consecutivo nunca se reutiliza.

## 4. Crear un documento (Autor)

1. Menú **Nuevo documento**.
2. Elige **área → subárea → tipo** y escribe el **título**. Verás la **vista previa del código**.
3. Crea el documento: queda en **Borrador** (Rev.00).
4. Sube el **PDF** (y, si quieres, el archivo fuente editable).
5. Inicia el flujo: asigna a quien **revisa** y a quien **aprueba**.
6. Sigue su avance en **Documentos**. Si lo rechazan, verás el comentario, corriges y lo reenvías.

> La **subárea (unidad) forma parte del código y no se puede cambiar** después. Elígela con cuidado.

## 5. Revisar y aprobar

- Entra a **Mis tareas** (el número naranja indica cuántas tienes).
- Abre el documento, lee el PDF y **aprueba** o **rechaza**. Al rechazar, el comentario es obligatorio.
- Cuando se completa la última aprobación, la revisión pasa a **Vigente** y la anterior queda **Obsoleta**.

## 6. Cambiar un documento vigente (Autor)

Desde el documento, crea una **nueva revisión**:
- **Mayor:** cambia el contenido o los requisitos. Pasa por todo el flujo.
- **Menor:** corrección editorial; aprobación abreviada del dueño del área.
En ambos casos el **motivo del cambio es obligatorio**. La revisión vigente sigue en uso hasta que se aprueba la nueva.

## 7. Revisión periódica

Cada documento debe revisarse cada **12 meses**. Cuando vence se marca **Vencido** (sigue vigente, pero aparece en *Reportes*).
Si no requiere cambios, el responsable propone **«Revisado sin cambios»** y el dueño del área lo decide.

## 8. Para el Administrador

- **Administración → Usuarios y roles:** *Nuevo usuario* (usuario, nombre, correo, roles y —para Autores— áreas).
  La **contraseña temporal se muestra una sola vez**: entrégala por un medio seguro.
- **Restablecer contraseña:** genera otra temporal, desbloquea la cuenta y cierra sus sesiones.
- **Desactivar:** la persona deja de entrar en su siguiente clic; no se borra nada de su historial.
- **Cargar catálogo estándar:** agrega las áreas, subáreas y tipos de Calidad. Es seguro repetirlo; solo agrega.
- **Bitácora:** quién hizo qué y cuándo (altas de usuario, inicios de sesión, aprobaciones…). Nunca guarda contraseñas.

## 9. Problemas frecuentes

| Situación | Qué hacer |
|---|---|
| «Usuario o contraseña incorrectos» | Revisa mayúsculas. Tras 5 fallos, espera 15 min o pide restablecerla. |
| No veo un documento | Solo aparecen los **vigentes**. Si debería estar vigente, avisa al Autor o a Calidad. |
| No puedo crear en un área | Tu cuenta no tiene esa área asignada. Pídela al Administrador. |
| La página no abre | Verifica que estás en la red de la planta y que la dirección es la correcta. |
| Algo no funciona o se ve mal | Anota **qué hacías, la pantalla y la hora**, y avisa al Administrador. |

*Este sistema controla documentos conforme a ISO 9001 cl. 7.5: solo la revisión **vigente** es válida para su uso.*
