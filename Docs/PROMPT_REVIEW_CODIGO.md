# Prompt de revisión de código — Sincronizador RUTX

> Copia el bloque completo y pégalo a un agente de IA de revisión de código
> (o úsalo como checklist manual). Apunta al repositorio
> `C:\MyProyects\Teknologix prueba\sincronizador_rutx`.

---

## PROMPT (copia desde aquí)

Eres un revisor senior de código. Revisa a fondo el proyecto **Rutx.Sincronizador**
(`C:\MyProyects\Teknologix prueba\sincronizador_rutx`): una API ASP.NET Core
que sincroniza ventas/cobranzas de una app móvil Flutter hacia la base de datos
Firebird de **Microsip ERP** (2.5, 3.0, 4.0 y 5.0), con panel web de instalación
en `/admin` (`localhost:5047`). Stack: .NET 10, Dapper, FirebirdSql.Data 10.3.4,
xUnit, HTML/JS vanilla en `wwwroot/admin.html`.

Busca **bugs, lógica incoherente, errores de UI, errores de consultas SQL y
debilidades en el control de errores**, reportando cada hallazgo con severidad
(crítica/alta/media/baja), archivo:línea, explicación y corrección sugerida.
NO propongas refactors cosméticos ni cambios de arquitectura sin justificar un
defecto real.

### 1) Lógica de negocio y transacciones
- `Services/VentaServicePv.cs`, `Services/CobranzaService.cs`, `Services/ClienteService.cs`, `Services/FolioService.cs`, `Services/FirebirdRetryPolicy.cs`
- ¿Las transacciones cubren TODAS las escrituras (header+detalle+impuestos+cobros+ligas+CxC)? ¿Hay commits/rollbacks correctos en cada ruta (éxito, error, deadlock)?
- ¿El retry (`FirebirdRetryPolicy`) es seguro con transacciones ya iniciadas? ¿Los contadores de folios y los bloques de folios (`FolioService`) son correctos bajo concurrencia?
- ¿El flujo de NO VENTA (segundo INSERT de `DOCTOS_PV` en VentaServicePv) y de cobranza a crédito (`DOCTOS_CC`, `DOCTOS_ENTRE_SIS`, `DOCTOS_PV_LIGAS`) es coherente con cómo Microsip espera los datos?
- ¿Hay cálculos de impuestos redondeados correctamente (decimales, factor compuesto de varios impuestos)?

### 2) Consultas SQL / Firebird (2.5 → 5.0)
- `Services/` (todos los INSERT/SELECT), `Services/AuditoriaCompatibilidadService.cs`, `Controllers/Web/AdminController.cs`
- ¿Cada INSERT cubre TODAS las columnas NOT NULL **sin default efectivo** (default de columna O del **dominio** `RDB$FIELDS`)? ¿El diccionario `ColumnasEscritasPorSync` de la auditoría coincide con los INSERTs reales?
- ¿Los valores escritos respetan las CHECK constraints de Microsip (`TIPO_DOCTO`, `ESTATUS`, `PROCESO_ORIGEN`, etc.) y las FKs?
- ¿`POSICION=-1` en `DOCTOS_PV_DET` puede causar problemas de ordenamiento en Microsip?
- ¿Las consultas a `RDB$*` son correctas para FB 2.5/3.0/4.0/5.0 (diferencias de `MON$VERSION`, dominios, BLOBs)? ¿El JOIN `RDB$RELATION_FIELDS.RDB$FIELD_SOURCE = RDB$FIELDS.RDB$FIELD_NAME` es correcto?
- ¿Hay riesgo de inyección SQL (interpolación de cadenas con datos del usuario o de la app)?
- ¿Las cadenas de conexión manejan rutas con espacios, comillas y caracteres especiales (revisa `ExtraerRutaBd` y `GET /api/v2/admin/conexion`)?

### 3) Control de errores
- `Services/SqlErrorClassifier.cs`, `Middleware/ErrorHandlingMiddleware.cs`
- ¿El clasificador cubre los mensajes reales de Firebird (FK, NOT NULL, CHECK, duplicado, ODS, conexión, deadlock)? ¿Hay falsos positivos (mensajes de negocio en español que coinciden con patrones en inglés)? ¿El orden de las comprobaciones es correcto?
- ¿Los HTTP status (409 FK/duplicado, 503 conexión/deadlock reintentable, 400 resto, 500 ODS) son apropiados y consistentes con lo que espera la app móvil?
- ¿Las excepciones de negocio (`FolioOcupadoException`, `DeadlockTransientException`, etc.) conservan su comportamiento original? ¿El recorrido de inner exceptions funciona para `DeadlockTransientException` envuelta en FbException?
- ¿La cola offline (`SyncQueueProcessor`) maneja payloads irreparables (dead-letter) o se queda reintentando para siempre?

### 4) UI del panel web (`wwwroot/admin.html`) y endpoints admin
- ¿Errores de JavaScript, objetos no definidos, race conditions en `fetch` (la auditoría se dispara al abrir), manipulación insegura de DOM (XSS con `innerHTML` desde datos del servidor)?
- ¿Los campos de solo lectura de la card Conexión se llenan y el guardado conserva la `FirebirdConnection` (¿algún flujo la puede dejar vacía)? ¿La card "Estado de compatibilidad" renderiza bien la sección nueva `NOT NULL`?
- ¿Responsividad y tamaños de objetos en pantallas pequeñas (celular)? ¿Estado de carga/error en cada llamada al API?
- ¿Los endpoints admin filtran/securizan la salida (no exponer passwords de la BD en respuestas)? (Nota: el panel hoy NO tiene autenticación — evalúa el riesgo.)

### 5) Compatibilidad universal (Fase "Firebird 2.5→5.0")
- `Services/FirebirdVersionDetector.cs` (offsets del ODS: major 18-19 & 0x7FFF, minor 62 si ODS<=11 si no 64), `Services/FirebirdVersionDetector.cs`
- ¿Los offsets del header .fdb son correctos para FB 2.5/3.0/4.0/5.0? ¿El mapeo ODS→versión tiene fallos (ej. ODS 13.0 vs 13.1)? ¿Maneja archivos bloqueados por el servidor Firebird?

### Formato de respuesta
Devuelve una tabla: | # | Severidad | Archivo:línea | Problema | Corrección sugerida |, seguida de un resumen con los 5 hallazgos más importantes priorizados. Si no encuentras defectos en una categoría, dilo explícitamente.

---

## CHECKLIST MANUAL (versión corta)

- [ ] Todos los INSERTs cubren NOT NULL sin default efectivo (columna + dominio)
- [ ] CHECKs/FKs de Microsip respetadas (`TIPO_DOCTO`, `ESTATUS`, `PROCESO_ORIGEN`)
- [ ] Transacciones: rollback en error, retry seguro, folios sin duplicados bajo concurrencia
- [ ] Clasificador de errores: sin falsos positivos, status codes correctos
- [ ] Panel: sin XSS, campos de solo lectura correctos, auditoría NOT NULL visible
- [ ] Version detector: offsets correctos 2.5→5.0
- [ ] Sin inyección SQL en interpolaciones
- [ ] `dotnet build` 0 errores y `dotnet test` todo verde antes de entregar
