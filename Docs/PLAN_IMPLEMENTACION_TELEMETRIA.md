# PLAN DE IMPLEMENTACIÓN — BD complementaria, eventos operativos y telemetría móvil

**Base:** `Plan 03 consolidado — BD complementaria, eventos operativos y telemetría móvil.md`
**Ramas:** `feature/telemetria-coordenadas` (creadas desde `origin/demo` en ambos repos)
**Repositorios locales:**

| Repo | Ruta local | Remoto |
|---|---|---|
| RutX-Sincronizador | `C:\Users\estra\...\RutX-Sincronizador` (repo actual) | `estr05/RutX-Sincronizador` |
| RutX-AppMovil | `C:\MyProyects\Teknologix prueba\appmovil_rutx` | `estr05/RutX-AppMovil` |

**Orden de ejecución (funcionalidad lo antes posible, sin romper dependencias):**

1. **Iteración 1** — Migración v007 + capa de datos del Sincronizador (base de todo).
2. **Iteración 2** — Servicios y endpoints de dispositivos y telemetría (el Sincronizador queda funcional de punta a punta, probable con tests HTTP).
3. **Iteración 3** — Outbox móvil: identidad de dispositivo, GPS, tabla local y envío.
4. **Iteración 4** — Integración en los 4 flujos móviles, pruebas end-to-end y documentación.

Cada iteración es un conjunto de commits separados por responsabilidad. No se inicia la iteración N+1 sin compilar y pasar los tests de la anterior.

---

# ITERACIÓN 1 — Migración v007 y capa de datos (Solo Sincronizador)

## Contexto
- La BD complementaria `C:\Microsip Extras\RUTX_COMPLEMENTARIA.db` existe y tiene migraciones v001–v006 aplicadas vía `Data/Web/WebMigrations.cs` + `Data/Web/WebSqliteMigrator.cs` (respaldo previo y validación ya implementados).
- Existen hoy: `web_users`, `web_zones`, `web_zone_sellers`, `web_notifications`, `web_audit_log`, `rutx_seller_sessions`, `rutx_seller_locations`, `rutx_no_sale_operations`, `rutx_media_files`, `rutx_cola_operaciones`, `rutx_ventas_sincronizadas`.
- `Data/Web/WebSqliteStore.cs` implementa `Data/Web/IWebSqliteStore.cs` y centraliza todas las consultas parametrizadas. Los records están en `Data/Web/WebStoreModels.cs`.

## Problema
- No existen las tablas del Plan 03: `rutx_contracts`, `rutx_customer_refs`, `rutx_mobile_devices`, `rutx_device_assignments`, `rutx_notification_recipients`, `rutx_notification_deliveries`, `rutx_notification_receipts`, `rutx_route_assignments`, `rutx_seller_events`, `rutx_route_closures`.
- `rutx_seller_sessions` no tiene `contract_id` ni `mobile_device_id`; `rutx_seller_locations` no tiene `event_id`, `source_event_type` ni `customer_id`.
- Sin estas tablas no hay dónde persistir telemetría, dispositivos ni notificaciones.

## Objetivo
- Dejar el esquema de la BD complementaria completo (alcance original del Plan 03 + restricciones CHECK de eventos), accesible mediante métodos testeables en `IWebSqliteStore`, sin tocar migraciones v001–v006 ni Firebird.

## Archivos Afectados
- [Modificar] `Data/Web/WebMigrations.cs` (agregar migración v007 al final del catálogo)
- [Modificar] `Data/Web/WebStoreModels.cs` (records nuevos)
- [Modificar] `Data/Web/IWebSqliteStore.cs` (métodos nuevos)
- [Modificar] `Data/Web/WebSqliteStore.cs` (implementación)

## Mutación de Datos / Contratos
- Esquema SQLite (v007), exactamente según §7–§8 del plan consolidado:
  - `rutx_contracts(id, contract_number UNIQUE, license_number UNIQUE NULL, license_key_hash UNIQUE NULL, account_name, status DEFAULT 'active', valid_from, valid_to, max_active_devices DEFAULT 1, created_at, updated_at)`
  - `rutx_customer_refs(id, contract_id FK RESTRICT, customer_id, customer_name, seller_id, route_id, zone_id, source_system DEFAULT 'microsip', status DEFAULT 'active', last_synced_at, created_at, updated_at, UNIQUE(contract_id, customer_id))`
  - `rutx_mobile_devices(id, device_id TEXT UNIQUE, platform, app_version, status DEFAULT 'active', token_hash NULL, created_at, updated_at)` *(definir columnas restantes alineadas al plan original)*
  - `rutx_device_assignments(id, device_id FK RESTRICT, contract_id FK RESTRICT, seller_id, device_number, valid_from, valid_to, status DEFAULT 'active', created_at, updated_at)` + índices únicos parciales `ux_rutx_device_active_assignment(device_id) WHERE status='active' AND valid_to IS NULL` y `ux_rutx_contract_active_device_number(contract_id, device_number) WHERE ...`
  - `rutx_notification_recipients`, `rutx_notification_deliveries`, `rutx_notification_receipts` (estados §11: `queued/sent/delivered/read/failed/expired`, `read`, etc.)
  - `rutx_route_assignments(seller_id, route_id, zone_id, vigencia)`
  - `rutx_seller_events(...)` con `client_event_id TEXT NOT NULL UNIQUE`, `event_type CHECK IN ('venta','no_venta','descarga_matutina','cierre_jornada')`, CHECKs de latitud/longitud/accuracy, `status DEFAULT 'received'` e índices `ix_..._seller_time`, `ix_..._session_type`, `ix_..._customer_time`, `ix_..._status` (SQL literal del §7).
  - `rutx_route_closures(cierre_movil_id UNIQUE, request_json, response_json, status, ...)`
  - ALTER aditivos: `rutx_seller_locations ADD COLUMN event_id TEXT NULL / source_event_type TEXT NULL / customer_id INTEGER NULL`; `rutx_seller_sessions ADD COLUMN contract_id INTEGER NULL / mobile_device_id INTEGER NULL`.
- No cambia ningún payload de API en esta iteración.
- `license_key` nunca se guarda en claro: solo `license_key_hash`.

## Plan de solución
1. Agregar `new(7, "telemetria-identidad-y-eventos", TelemetriaIdentidadYEventos)` al catálogo con el SQL del §7/§8 (CREATE TABLE IF NOT EXISTS + índices + ALTERs idempotentes).
2. Crear records en `WebStoreModels.cs`: `RutxContract`, `RutxCustomerRef`, `RutxMobileDevice`, `RutxDeviceAssignment`, `NotificationRecipientRow`, `NotificationDeliveryRow`, `NotificationReceiptRow`, `RouteAssignmentRow`, `SellerEventRow`, `RouteClosureRow`.
3. En `IWebSqliteStore.cs` exponer métodos: `EnsureContractAsync`, `GetContractByNumberAsync`, `UpsertCustomerRefAsync`, `FindActiveCustomerRefAsync(contractId, customerId)`, `RegisterEventAsync(SellerEventRow)` (retorna conflicto de idempotencia si mismo `client_event_id` con distinto `payload_hash`), `GetEventByClientEventIdAsync`, `InsertLocationForEventAsync(...)`, métodos CRUD de devices/assignments/recipients/deliveries/receipts/closures.
4. Implementar en `WebSqliteStore.cs` con Dapper/SQLite parametrizado, respetando el estilo existente y `PRAGMA foreign_keys = ON`.

## Ejecución (Instrucciones precisas para el Coder)
1. En `Data/Web/WebMigrations.cs`: agregar la entrada `new(7, ...)` al final de la lista `All` y la constante privada `TelemetriaIdentidadYEventos` con el SQL de §7 y §8 del plan (copiar literal; ajustar solo `rutx_mobile_devices` a las columnas reales necesarias). Actualizar el comentario de cabecera del archivo con la descripción de v007.
2. En `Data/Web/WebStoreModels.cs`: agregar los records listados, con tipos `int?`/`string?` donde la columna sea nullable.
3. En `Data/Web/IWebSqliteStore.cs`: agregar los métodos nuevos agrupados bajo una región comentada `// TELEMETRÍA Y DISPOSITIVOS (v007)` para revisión fácil.
4. En `Data/Web/WebSqliteStore.cs`: implementar cada método con SQL parametrizado (`@param`). En `InsertLocationForEventAsync`, insertar en `rutx_seller_locations` dentro de la misma transacción que registra el evento cuando haya coordenada válida.
5. No modificar nada dentro de las migraciones v001–v006 ni su numeración.

## Calidad y Pruebas Automatizadas (QA & Testing)
- **Tests:** crear `Rutx.Sincronizador.Tests/Web/WebMigracionesV007Tests.cs`: aplicar migraciones desde BD vacía hasta v007 y desde una BD v006 simulada; verificar tablas, índices, CHECKs (insertar latitud 91 debe fallar; event_type 'otro' debe fallar) y unicidad de `client_event_id`. Crear `Rutx.Sincronizador.Tests/Web/WebSqliteStoreTelemetriaTests.cs`: registrar evento nuevo, reintento con mismo ID/hash devuelve resultado previo, mismo ID con otro hash devuelve conflicto.
- **Linting/build:** `dotnet build rutx-sincronizador.sln` sin warnings nuevos.
- **Comando:** `dotnet test` verde antes de commit.
- **Cobertura:** rutas críticas: unicidad/idempotencia de eventos, CHECK constraints, índices únicos parciales de assignments.

## Validación y Casos Borde
- **Escenario ideal:** correr migrador sobre copia real de `RUTX_COMPLEMENTARIA.db` v006 → aplica v007, conteos de tablas correctos, integridad OK.
- **Borde 1:** migrar dos veces → v007 es idempotente (IF NOT EXISTS / ALTER protegido) y no duplica.
- **Borde 2:** BD con FK desactivadas o filas huérfanas heredadas → el migrador valida integridad y falla con mensaje claro antes de escribir.

## Criterios de aceptación
- [ ] `SchemaVersionAsync() == 7` tras aplicar migraciones desde cero y desde v006.
- [ ] Ninguna línea de v001–v006 modificada (diff limpio).
- [ ] Insert de evento duplicado con mismo hash no crea segunda fila; con distinto hash retorna conflicto.
- [ ] Firebird sin ninguna escritura nueva.

## Restricciones
- No modificar Firebird/Microsip.
- No editar ni renumerar v001–v006.
- La BD complementaria nunca dentro de `C:\Microsip datos`.
- Respetar convenciones existentes de `WebSqliteStore` (fechas TEXT ISO 8601).

## Rollback
- Revertir los 4 archivos modificados (`git checkout main -- Data/Web/...`) y restaurar el respaldo `.db` creado por `WebSqliteMigrator` antes de v007.

## Resultado esperado
- Esquema v007 completo aplicado y consultable; toda la lógica de persistencia lista para que la Iteración 2 solo agregue servicios/controladores.

---

# ITERACIÓN 2 — Dispositivos y endpoint de telemetría (Solo Sincronizador)

## Contexto
- Tras Iteración 1 existen tablas y métodos de store. Ya existe autenticación móvil JWT Bearer (`Controllers/Movil/AuthController.cs`, `api/auth/login`, `api/auth/me`) y controladores `api/v1/pv/ventas`, `api/v1/pv/noventa`, `api/v1/routes/sync`, `api/v1/routes/close` cuya semántica NO debe cambiar.
- `Program.cs` registra servicios con `AddScoped` (líneas ~226–303); ahí se agregarán los nuevos.

## Problema
- No hay forma de que un teléfono se registre de forma controlada ni de enviar un evento de coordenadas: no existen `DevicesController`, `TelemetryController`, `DeviceRegistryService` ni `TelemetryService`.

## Objetivo
- Exponer `POST /api/v1/devices/activate` y `POST /api/v1/telemetry/events`, con validación de contrato/licencia/cupo/asignación, resolución servidor-side de `contract_id`/`mobile_device_id`/`seller_id`, deduplicación por `client_event_id` y escritura de ubicación en la misma transacción.

## Archivos Afectados
- [Crear] `Models/DeviceActivationDtos.cs`
- [Crear] `Models/TelemetryEventDtos.cs`
- [Crear] `Services/DeviceRegistryService.cs` (+ interfaz `IDeviceRegistryService`)
- [Crear] `Services/TelemetryService.cs` (+ interfaz `ITelemetryService`)
- [Crear] `Controllers/Movil/DevicesController.cs`
- [Crear] `Controllers/Movil/TelemetryController.cs`
- [Modificar] `Program.cs` (2 registros DI)
- [Crear] `Docs/CONTRATOS_NOTIFICACIONES_Y_TELEMETRIA.md`

## Mutación de Datos / Contratos
- Activación `POST /api/v1/devices/activate` (anónimo pero con credenciales de activación; decidir: requerir JWT de vendedor existente + clave de activación):
```json
{ "contract_number": "CONTRATO-001", "activation_key": "...", "device_installation_id": "uuid", "platform": "android", "app_version": "1.0.0" }
```
  Respuestas: `200 { "device_number": 2, "status": "active" }` · `409 contrato_sin_cupo` · `403 contrato_invalido`.
- Telemetría `POST /api/v1/telemetry/events` ([Authorize], JWT de vendedor), payload mínimo §9 del plan (`client_event_id`, `event_type`, `customer_id?`, `related_entity_id?`, `latitude?`, `longitude?`, `accuracy?`, `occurred_at`, `metadata?`). El servidor resuelve `seller_id`, `contract_id`, `mobile_device_id` desde el JWT + asignación activa; ignora `device_number` como autorización.
  Respuestas: `201 {status:"received"}` · `200 {status:"processed", duplicate:true}` (mismo ID+mismo hash) · `409 idempotency_conflict` · `422 customer_not_assigned | invalid_coordinates | invalid_event_type` · `403 device_not_authorized`.
- Estados del evento: `received → processed | retryable_failed | permanent_failed`.

## Plan de solución
1. DTOs con validaciones de data annotations (rangos de coordenadas, enum string estricto).
2. `DeviceRegistryService`: valida contrato vigente (`valid_to`), calcula cupo activo vs `max_active_devices`, cierra asignación previa (`valid_to=now, status='revoked'`) para reemplazo, crea dispositivo+asignación. Nunca borra historial. Hash de clave de activación con PBKDF2 igual que contraseñas web.
3. `TelemetryService`: valida dispositivo autorizado (existe, activo, asignación vigente), valida `customer_id` contra `rutx_customer_refs` solo para `venta`/`no_venta`, calcula SHA-256 del payload, delega en `IWebSqliteStore.RegisterEventAsync` + `InsertLocationForEventAsync`.
4. Controladores delgados: solo mapeo HTTP ↔ servicio y códigos de estado.

## Ejecución (Instrucciones precisas para el Coder)
1. Crear los 4 archivos nuevos de Models/Services/Controllers con los nombres exactos indicados.
2. En `Program.cs`, junto a los registros web (línea ~303), agregar `builder.Services.AddScoped<IDeviceRegistryService, DeviceRegistryService>();` y `builder.Services.AddScoped<ITelemetryService, TelemetryService>();`.
3. `DevicesController` con ruta `[Route("api/v1/devices")]` y acción `[HttpPost("activate")]`; `TelemetryController` con `[Route("api/v1/telemetry")]`, `[HttpPost("events")]` y `[Authorize]`.
4. En `TelemetryService`, orden estricto de validación: 1) tipo válido → 2) coordenadas (si vienen, en rango) → 3) dispositivo/contrato autorizados → 4) `customer_id` obligatorio y referenciado para venta/no_venta → 5) idempotencia → 6) persistencia evento+ubicación en transacción.
5. Documentar en `Docs/CONTRATOS_NOTIFICACIONES_Y_TELEMETRIA.md` los payloads, estados, códigos de error y reglas de retención.

## Calidad y Pruebas Automatizadas (QA & Testing)
- **Tests:** `Rutx.Sincronizador.Tests/Web/TelemetryEndpointTests.cs` (WebApplicationFactory o test de servicio directo): los 16 casos de la tabla §15 del plan (duplicado, conflicto de hash, GPS nulo, cliente no asignado, dispositivo no registrado, cupo agotado, coordenada inválida, fuera de orden…). `Rutx.Sincronizador.Tests/Web/DeviceRegistryTests.cs`: alta, reemplazo, bloqueo, cupo.
- **Comando:** `dotnet test`.
- **Cobertura:** 100% de las ramas de validación de `TelemetryService.Validate*` y del cálculo de cupo.

## Validación y Casos Borde
- **Escenario ideal:** curl con JWT válido + payload de venta → 201, fila en `rutx_seller_events` + fila en `rutx_seller_locations` con `event_id`.
- **Borde 1:** red cae a mitad del POST → el móvil reintenta con mismo `client_event_id` → recibe 200 duplicate, sin segunda fila.
- **Borde 2:** doble clic rápido en la app → dos POST simultáneos → UNIQUE + transacción garantizan una sola fila; uno recibe 201 y otro 200-duplicate (o 409 si hashes difieren).

## Criterios de aceptación
- [ ] Los 4 endpoints comerciales existentes (`pv/ventas`, `pv/noventa`, `routes/sync`, `routes/close`) sin ningún cambio de contrato (diff no los toca salvo DI).
- [ ] Ningún evento aceptado con dispositivo no autorizado, cliente no referenciado o coordenada fuera de rango.
- [ ] Ningún secreto (licencia/clave) devuelto por respuesta alguna ni guardado en claro.

## Restricciones
- No agregar campos GPS al JSON de venta/noventa ni multipart de fotos.
- No auto-autorizar teléfonos desde el body del evento.
- Mantener API-first: sin lógica de negocio en controladores.

## Rollback
- Eliminar los archivos creados y revertir `Program.cs`. Las tablas v007 pueden permanecer (son aditivas e inertes sin los endpoints).

## Resultado esperado
- Un curl de prueba puede activar un dispositivo y enviar un evento que queda persistido una sola vez en la BD complementaria, verificable con sqlite3.

---

# ITERACIÓN 3 — Outbox móvil: identidad, GPS y envío (Solo AppMovil)

## Contexto
- Flutter (`rutx_movil`), sqflite versión de esquema `_version = 17` en `lib/core/database/app_database.dart`. Existe `cola_sincronizacion(tipo, entidad_id, estado, prioridad, reintentos, ...)` y `SyncQueueProcessor.registerHandler/enqueue` usados por `lib/core/network/sync_service.dart` (hoy solo handlers `venta` y `cobranza`).
- `lib/core/storage/local_storage.dart` guarda token, vendedor, cajero/caja/almacén/sucursal. NO guarda `device_installation_id` ni `contract_number`.
- Permisos Android: solo `INTERNET`. Sin paquete de geolocalización en `pubspec.yaml`.

## Problema
- La app no puede producir ni conservar eventos de coordenadas: falta tabla outbox, entidad/DAO, proveedor de ubicación, identidad de instalación y repositorio de telemetría con `captureAndQueue()`.

## Objetivo
- Que la app genere el evento (con o sin GPS, con o sin red), lo conserve en SQLite local como outbox, lo encole como referencia tipo `telemetria` y lo envíe a `POST /api/v1/telemetry/events` con reintento automático, sin bloquear jamás la operación comercial.

## Archivos Afectados
- [Crear] `lib/core/database/entities/telemetria_pendiente_entity.dart`
- [Crear] `lib/core/database/daos/telemetria_dao.dart`
- [Crear] `lib/core/location/location_provider.dart`
- [Crear] `lib/core/device/device_identity_provider.dart`
- [Crear] `lib/core/network/telemetry_repository.dart`
- [Modificar] `lib/core/database/app_database.dart` (migración v17→v18 + CREATE en `_onCreate`; excluir tabla de `limpiarDatosDelDia()`)
- [Modificar] `lib/core/storage/local_storage.dart` (claves `device_installation_id`, `contract_number`, `device_number`)
- [Modificar] `lib/core/network/sync_service.dart` (handler `telemetria`)
- [Modificar] `pubspec.yaml` (agregar `geolocator: ^13.x`)
- [Modificar] `android/app/src/main/AndroidManifest.xml` (`ACCESS_COARSE_LOCATION`, `ACCESS_FINE_LOCATION`)

## Mutación de Datos / Contratos
- Tabla local `telemetria_pendiente` (§10 del plan, literal):
```sql
CREATE TABLE telemetria_pendiente (
  client_event_id TEXT PRIMARY KEY,
  event_type TEXT NOT NULL,
  customer_id INTEGER NULL,
  related_entity_id TEXT NULL,
  seller_id INTEGER NOT NULL,
  contract_number TEXT NOT NULL,
  device_installation_id TEXT NOT NULL,
  latitude REAL NULL, longitude REAL NULL, accuracy REAL NULL,
  occurred_at TEXT NOT NULL,
  metadata_json TEXT NULL,
  estado TEXT NOT NULL DEFAULT 'pendiente',
  reintentos INTEGER NOT NULL DEFAULT 0,
  ultimo_error TEXT NULL,
  creado_en TEXT NOT NULL,
  enviado_en TEXT NULL
);
```
- Cola: fila `cola_sincronizacion(tipo:'telemetria', entidadId: client_event_id)`; el payload vive SOLO en `telemetria_pendiente`.
- Estado local: `pendiente | enviado | error`.
- Contrato HTTP: el definido en Iteración 2.

## Plan de solución
1. Migración incremental v18 en `_onUpgrade` (crear tabla) y en `_onCreate` (para instalaciones nuevas). En `limpiarDatosDelDia()` (línea ~618) NO borrar `telemetria_pendiente`.
2. `DeviceIdentityProvider`: genera UUID con el paquete `uuid` (ya presente) y lo persiste en SharedPreferences vía `LocalStorage`; nunca IMEI.
3. `LocationProvider`: abstracción con implementación sobre `geolocator`; timeout de ~5 s; ante permiso denegado/servicio apagado devuelve `null` (nunca lanza).
4. `TelemetryRepository.captureAndQueue({eventType, customerId?, relatedEntityId?, metadata?})`: obtiene ubicación (best-effort), arma payload con identidad local, inserta en DAO, encola en cola_sincronizacion y dispara intento inmediato si hay conexión. Falla de GPS/red ≠ falla de la función.
5. Handler en `SyncService`: `registerHandler('telemetria', (id) => TelemetryRepository().sendOne(id))` que hace POST y marca `enviado`/`error`.

## Ejecución (Instrucciones precisas para el Coder)
1. `pubspec.yaml`: agregar `geolocator: ^13.0.1`; ejecutar `flutter pub get`.
2. `AndroidManifest.xml`: agregar los dos permisos de ubicación (no background location).
3. `app_database.dart`: subir `_version` a 18; en `_onUpgrade` agregar bloque `if (oldVersion < 18)` con el CREATE TABLE; replicar en `_onCreate`; verificar que `limpiarDatosDelDia()` no incluya esta tabla.
4. Crear entity + DAO (métodos: `insert`, `getById`, `pendientes()`, `marcarEnviado(id, fechaIso)`, `marcarError(id, error, fechaIso)`, `incrementarReintento(id, error)`), siguiendo el patrón de `venta_dao.dart`.
5. Crear `location_provider.dart`, `device_identity_provider.dart` y `telemetry_repository.dart` con la firma exacta `captureAndQueue` del plan (§4).
6. En `sync_service.dart` agregar el registro del handler `telemetria` junto a los existentes.

## Calidad y Pruebas Automatizadas (QA & Testing)
- **Tests:** `test/core/database/telemetria_dao_test.dart` (sqflite_common_ffi, ya es dependencia): insertar/duplicar PK/marcar estados. `test/core/network/telemetry_repository_test.dart` con `LocationProvider` y `Dio` mockeados: GPS denegado → guarda con coords null; sin red → queda pendiente y en cola; reintento exitoso → marcado enviado y fila de cola completada.
- **Análisis:** `flutter analyze` sin errores (flutter_lints ^5).
- **Comando:** `flutter test`.
- **Cobertura:** captura con/sin GPS, envío con/sin red, idempotencia de `client_event_id`.

## Validación y Casos Borde
- **Escenario ideal:** confirmar una venta con red y GPS → fila en `telemetria_pendiente` pasa a `enviado` en segundos; en la BD complementaria aparece el evento.
- **Borde 1:** modo avión → evento queda `pendiente` con fila en cola; al reconectar, `ConnectionStateService` dispara procesamiento y se envía.
- **Borde 2:** permiso denegado o timeout de GPS → evento con `latitude/longitude` null y metadata `{gps:"denegado"}`; la venta se registra igualmente.

## Criterios de aceptación
- [ ] `captureAndQueue()` nunca lanza excepción hacia el flujo de negocio.
- [ ] Desinstalar/reinstalar conserva identidad vía respaldo de SharedPreferences (o al menos genera UUID nuevo estable por instalación; nunca IMEI).
- [ ] `limpiarDatosDelDia()` no elimina eventos pendientes.
- [ ] `flutter analyze` y `flutter test` verdes.

## Restricciones
- No rastreo continuo de ubicación: solo muestra puntual al momento de la acción.
- No modificar pantallas ni BLoCs de presentación en esta iteración.
- Sin nuevas librerías además de `geolocator`.

## Rollback
- Revertir los archivos modificados/creados (git). Si la app ya corrió con v18 en un dispositivo de prueba, bajar de versión requiere reinstalar (sqflite no soporta downgrade) — impacto solo local.

## Resultado esperado
- La app produce, conserva y envía eventos de telemetría de forma autónoma; los 4 flujos aún no llaman a `captureAndQueue` (eso es la Iteración 4).

---

# ITERACIÓN 4 — Integración en flujos, E2E y documentación

## Contexto
- Iteraciones 1–3 completas: backend acepta eventos; la app tiene `TelemetryRepository.captureAndQueue()` y el handler `telemetria` registrado.
- Puntos de integración ya identificados y verificados: `SalesRepository.saveSaleLocally/saveAndSyncSale` (líneas ~38/~56), `SummaryRepository.sendClosingData` (línea ~40), `SyncRepository.downloadMorningData/aplicarDescargaIncremental` (líneas ~21/~238).

## Problema
- Ninguna acción real del vendedor genera todavía un evento: falta invocar `captureAndQueue` desde cada repositorio tras aceptarse la operación local.

## Objetivo
- Que las cuatro acciones (descarga matutina, venta, no-venta, cierre de jornada) generen exactamente un evento cada una, sin alterar su saga comercial ni sus contratos.

## Archivos Afectados
- [Modificar] `lib/features/sync/data/sync_repository.dart` (invocar `descarga_matutina` tras éxito de descarga)
- [Modificar] `lib/features/sales/data/sales_repository.dart` (invocar `venta` o `no_venta` según `esNoVenta`, con `customerId` obligatorio y `relatedEntityId = ventaMovilId`)
- [Modificar] `lib/features/summary/data/summary_repository.dart` (invocar `cierre_jornada` al aceptar cierre, `relatedEntityId = cierreMovilId`)
- [Modificar] `Docs/CONTRATOS_NOTIFICACIONES_Y_TELEMETRIA.md` (cerrar documentación)
- Opcional backend: `Controllers/Movil/MensajesController.cs` y `Services/Web/RouteMonitoringWebService.cs` para consumir eventos/ubicaciones en monitoreo (si el tiempo lo permite; puede quedar para otra rama).

## Mutación de Datos / Contratos
- Ninguna nueva. Solo llamadas internas. Los JSON comercial y multipart de fotos permanecen intactos.

## Plan de solución
1. Cada repositorio llama a `captureAndQueue` DESPUÉS de que su operación local fue aceptada/guardada, envuelta en try/catch que solo registra log (la telemetría jamás revierte la operación).
2. Regla de tipos: `esNoVenta == true → no_venta`, else `venta`; ambos con `customerId` no nulo (si es nulo, no se envía evento y se loguea).
3. Verificación manual E2E con el Sincronizador local + dispositivo/emulador.

## Ejecución (Instrucciones precisas para el Coder)
1. En `sales_repository.dart`, dentro de `saveAndSyncSale` (y también de `saveSaleLocally` para modo offline), después del guardado exitoso, invocar `TelemetryRepository().captureAndQueue(eventType: venta.esNoVenta ? TelemetryEventType.noSale : TelemetryEventType.sale, customerId: venta.clienteId, relatedEntityId: venta.ventaMovilId)` en try/catch.
2. En `sync_repository.dart`, al final exitoso de `aplicarDescargaIncremental`, invocar `captureAndQueue(eventType: TelemetryEventType.morningDownload)`.
3. En `summary_repository.dart`, tras aceptar `sendClosingData`, invocar `captureAndQueue(eventType: TelemetryEventType.dayClose, relatedEntityId: cierreMovilId)`.
4. Actualizar documentación con resultados de las pruebas E2E.

## Calidad y Pruebas Automatizadas (QA & Testing)
- **Tests:** ampliar `telemetry_repository_test.dart` con un test de integración por flujo usando repos mockeados: cada flujo genera exactamente 1 evento del tipo correcto. Backend: regresión completa `dotnet test`.
- **Comandos pre-commit:** `flutter analyze && flutter test` (móvil) · `dotnet build && dotnet test` (sincronizador).
- **Cobertura:** los 4 flujos × (éxito / GPS denegado / sin red).

## Validación y Casos Borde
- **Escenario ideal:** jornada simulada completa: descargar → vender → no-vender → cerrar; en `rutx_seller_events` aparecen exactamente 4 filas con los 4 `event_type` distintos y sus ubicaciones relacionadas.
- **Borde 1:** venta offline durante toda la jornada → 0 filas en servidor hasta reconexión; luego llegan todas con `occurred_at` originales (fuera de orden → se conservan).
- **Borde 2:** doble confirmación rápida de venta → 2 ventas pero 1 evento (segunda llamada con mismo `relatedEntityId`+timestamp genera `client_event_id` distinto: aceptar 2 eventos es correcto aquí; la deduplicación fuerte aplica a reintentos del MISMO `client_event_id`).

## Criterios de aceptación
- [ ] Cada acción genera exactamente un evento con su `event_type` correcto.
- [ ] Ningún fallo de GPS/red bloquea o revierte venta, no-venta, descarga o cierre.
- [ ] Regresión completa de tests de ambos repos en verde.
- [ ] Firebird sin ninguna escritura de telemetría (verificado en código y logs).

## Restricciones
- No alterar la saga de fotos de no-venta ni los endpoints `pv/*` y `routes/*`.
- No agregar pantallas nuevas ni cambios visuales.

## Rollback
- Revertir los 3 repositorios móviles modificados; el backend de Iteración 2 puede quedarse (los eventos simplemente dejan de llegar).

## Resultado esperado
- Sistema completo funcionando: el dispositivo produce el evento, el Sincronizador lo valida y persiste una sola vez en `C:\Microsip Extras\RUTX_COMPLEMENTARIA.db`, y la operación comercial nunca se detiene. Definición de hecho del plan consolidado cumplida.

---

## Notas de evaluación (hallazgos del modo lectura)

| Punto del plan | Estado real encontrado |
|---|---|
| Migraciones hasta v006 y `WebSqliteMigrator` | ✔ Confirmado (`Data/Web/WebMigrations.cs`) |
| Tablas v007 (contracts, devices, events, closures…) | ✘ No existen — se crean en Iteración 1 |
| Endpoints `api/v1/pv/ventas`, `noventa`, `routes/sync`, `routes/close` | ✔ Existen con esos nombres exactos |
| Auth móvil JWT | ✔ Existe (`AuthController`, Bearer en `Program.cs`) |
| Estructura móvil (`features/sales|sync|summary/data/...`, `core/network/sync_service.dart`, `cola_sincronizacion`) | ✔ Coincide 1:1 con las rutas del plan |
| DB local versión actual | 17 → la outbox será v18 |
| Geolocalización / permisos | ✘ No existe `geolocator`; manifest solo tiene INTERNET |
| Identidad de dispositivo | ✘ `LocalStorage` no guarda `device_installation_id` |
| Handlers de cola | Solo `venta` y `cobranza`; agregar `telemetria` |

**Advertencias operativas:**
- El repo móvil tenía cambios sin commitear (`.gitignore`, `android/app/build.gradle.kts`, `key.properties.example`) en `cloudflare-tunnel`; esos cambios viajaron contigo a la rama nueva. Commítalos o haz stash antes de mezclar trabajo de telemetría.
- Ambas ramas fueron creadas desde `origin/demo` tal como exige el plan; verifica que `demo` sea la base que quieres (la móvil podría estar más avanzada en `dev`).
