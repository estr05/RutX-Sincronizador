# Diseño Técnico — Cola Offline (Sincronizador RUTX)

## 1. Visión General

El sistema opera en un entorno donde los vendedores capturan datos en ruta con conectividad intermitente. La cola offline garantiza que **ninguna operación se pierda** aunque la red o Firebird no estén disponibles.

### Arquitectura de Doble Capa

```
┌──────────────────┐      ┌──────────────────────┐      ┌──────────────────┐
│    App Móvil     │ ───▶ │   Sincronizador      │ ───▶ │   Microsip       │
│  (SQLite Local)  │      │   (API + Cola)       │      │   (Firebird)     │
└──────────────────┘      └──────────────────────┘      └──────────────────┘
        │                           │                           │
        ▼                           ▼                           ▼
   Cola Local                  Cola Server                  Base de Datos
   Persiste en                 Persiste en                 Persiste en
   dispositivo                 SQLite server               Firebird ERP
```

### Flujo con Conexión Estable

```
Móvil captura venta
    │
    ▼
POST /api/v1/ventas → Sincronizador
    │
    ▼
ColaOfflineService.AgregarOperacionAsync()
    │
    ▼
SQLite: Operación PENDIENTE
    │
    ▼
BackgroundSyncService procesa cada 5s
    │
    ▼
Firebird: INSERT en DOCTOS_VE + DOCTOS_VE_DET
    │
    ▼
SQLite: Operación → COMPLETADO
    │
    ▼
Móvil consulta estado → OK
```

### Flujo con Caída de Red / Firebird

```
Móvil captura venta
    │
    ▼
POST /api/v1/ventas → Sincronizador
    │
    ▼
ColaOfflineService.AgregarOperacionAsync()
    │
    ▼
SQLite: Operación PENDIENTE
    │
    ▼
BackgroundSyncService intenta procesar
    │
    ├──▶ ❌ Firebird no disponible
    │
    ▼
SQLite: Intentos = 1, SiguienteReintento = +1s
    │
    ▼
BackgroundSyncService reintenta...
    │
    ├──▶ ❌ Firebird sigue caído
    │
    ▼
SQLite: Intentos = 2, SiguienteReintento = +2s
    │
    ... (backoff exponencial)
    │
    ▼
Firebird vuelve → INSERT exitoso
    │
    ▼
SQLite: Operación → COMPLETADO
```

---

## 2. Tabla ColaOperaciones (SQLite)

### Esquema

```sql
CREATE TABLE ColaOperaciones (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    OperacionId         TEXT    NOT NULL UNIQUE,    -- GUID para idempotencia
    TipoOperacion       TEXT    NOT NULL,           -- VENTA, CLIENTE, CIERRE
    Payload             TEXT    NOT NULL,           -- JSON completo de la operación
    Estado              TEXT    NOT NULL DEFAULT 'PENDIENTE',
    Intentos            INTEGER NOT NULL DEFAULT 0,
    MaxIntentos         INTEGER NOT NULL DEFAULT 5,
    SiguienteReintento  TEXT    NOT NULL,           -- ISO 8601 timestamp
    ErrorUltimoIntento  TEXT,                       -- Descripción del último error
    FechaCreacion       TEXT    NOT NULL,           -- ISO 8601
    FechaModificacion   TEXT    NOT NULL            -- ISO 8601
);

-- Índices para consultas frecuentes del BackgroundService
CREATE INDEX IX_ColaOperaciones_Estado ON ColaOperaciones(Estado);
CREATE INDEX IX_ColaOperaciones_SiguienteReintento ON ColaOperaciones(SiguienteReintento);
CREATE INDEX IX_ColaOperaciones_TipoOperacion ON ColaOperaciones(TipoOperacion);
```

### Campos Descripción

| Campo | Tipo | Descripción |
|-------|------|-------------|
| `Id` | INTEGER PK | Auto-increment, identificador interno |
| `OperacionId` | TEXT UNIQUE | GUID único, idempotencia ante duplicados |
| `TipoOperacion` | TEXT | `VENTA`, `CLIENTE` o `CIERRE` |
| `Payload` | TEXT | JSON serializado con todos los datos |
| `Estado` | TEXT | `PENDIENTE`, `PROCESANDO`, `COMPLETADO`, `FALLIDO` |
| `Intentos` | INTEGER | Veces que se ha intentado procesar |
| `MaxIntentos` | INTEGER | Límite antes de marcar FALLIDO permanente |
| `SiguienteReintento` | TEXT | Cuándo intentar de nuevo (ISO 8601) |
| `ErrorUltimoIntento` | TEXT | Mensaje del último error (nullable) |
| `FechaCreacion` | TEXT | Cuándo se recibió la operación |
| `FechaModificacion` | TEXT | Última actualización del registro |

---

## 3. Estados y Transiciones

```
                    ┌─────────────┐
                    │  PENDIENTE  │
                    └──────┬──────┘
                           │ BackgroundService toma la operación
                           ▼
                    ┌─────────────┐
             ┌─────│ PROCESANDO  │─────┐
             │     └─────────────┘     │
             │                         │
        ✅ Éxito                  ❌ Error
             │                         │
             ▼                         ▼
      ┌─────────────┐          ┌─────────────┐
      │ COMPLETADO  │          │ Reintentar? │
      └─────────────┘          └──────┬──────┘
                                      │
                               ┌──────┴──────┐
                               │             │
                          Sí              No (max intentos)
                               │             │
                               ▼             ▼
                        ┌───────────┐  ┌───────────┐
                        │ PENDIENTE │  │  FALLIDO  │
                        │(reagenda) │  │(permanente│
                        └───────────┘  └───────────┘
```

### Definición de Estados

| Estado | Descripción |
|--------|-------------|
| `PENDIENTE` | Recién recibida, espera a ser procesada |
| `PROCESANDO` | Siendo escrita en Firebird |
| `COMPLETADO` | Insertada exitosamente en Firebird |
| `FALLIDO` | Error después de `MaxIntentos` reintentos |

---

## 4. Estrategia de Reintentos

### Backoff Exponencial con Jitter

```
delay = min(baseDelay * 2^intentos, maxDelay) + jitter
```

| Intento | Delay Base | Jitter (0-1s) | Total Máximo |
|---------|-----------|---------------|--------------|
| 1 | 1s | +0-1s | ~1-2s |
| 2 | 2s | +0-1s | ~2-3s |
| 3 | 4s | +0-1s | ~4-5s |
| 4 | 8s | +0-1s | ~8-9s |
| 5 | 16s | +0-1s | ~16-17s |

**Configuración:**
- `BaseDelay`: 1 segundo
- `MaxDelay`: 30 segundos
- `MaxIntentos`: 5 (configurable por operación)

### Jitter

El jitter evita "thundering herd" cuando múltiples operaciones fallan al mismo tiempo:

```csharp
var jitter = new Random().NextDouble(); // 0.0 a 1.0
var delay = Math.Min(baseDelay * Math.Pow(2, intentos), maxDelay) + jitter;
```

---

## 5. Detección de Duplicados

Cada tipo de operación tiene un identificador único para evitar procesar el mismo dato dos veces:

| Operación | Campo Idempotencia | Descripción |
|-----------|-------------------|-------------|
| `VENTA` | `venta_movil_id` | ID generado por el móvil, único por venta |
| `CLIENTE` | `nombre` + `vendedor_id` | Combinación única de cliente por ruta |
| `CIERRE` | `vendedor_id` + `fecha_hora` | Un cierre por vendedor por día |

### Flujo de Duplicado

```
Operación llega al Sincronizador
    │
    ▼
¿Existe operación COMPLETADA con mismo idempotencia?
    │
    ├──▶ Sí → Retornar 200 OK (ya procesada, idempotente)
    │
    └──▶ No → Insertar en cola como PENDIENTE
```

---

## 6. API Endpoints de la Cola

### POST /api/v1/queue
Registra una operación en la cola.

**Request:**
```json
{
  "tipoOperacion": "VENTA",
  "idempotencia": "VTA-99823",
  "payload": { ... }
}
```

**Response (201):**
```json
{
  "operacionId": "550e8400-e29b-41d4-a716-446655440000",
  "estado": "PENDIENTE",
  "mensaje": "Operación registrada en cola"
}
```

### GET /api/v1/queue/status/{operacionId}
Consulta el estado de una operación.

**Response (200):**
```json
{
  "operacionId": "550e8400-e29b-41d4-a716-446655440000",
  "estado": "COMPLETADO",
  "intentos": 1,
  "fechaCreacion": "2026-06-26T10:30:00Z",
  "fechaModificacion": "2026-06-26T10:30:01Z"
}
```

### GET /api/v1/queue/failed
Lista operaciones en estado FALLIDO.

### POST /api/v1/queue/retry/{operacionId}
Reintenta manualmente una operación fallida.

### DELETE /api/v1/queue/{operacionId}
Elimina una operación de la cola (solo COMPLETADO o FALLIDO).

---

## 7. Background Service

### Ciclo de Proceso

```csharp
while (!stoppingToken.IsCancellationRequested)
{
    // 1. Obtener operaciones PENDIENTE con SiguienteReintento <= DateTime.UtcNow
    // 2. Para cada operación:
    //    a. Marcar como PROCESANDO
    //    b. Según TipoOperacion, ejecutar inserción en Firebird
    //    c. Si éxito → marcar COMPLETADO
    //    d. Si error → incrementar Intentos, calcular SiguienteReintento
    //    e. Si Intentos >= MaxIntentos → marcar FALLIDO
    // 3. Esperar 5 segundos
    
    await Task.Delay(5000, stoppingToken);
}
```

### Intervalo de Proceso

- **Default:** 5 segundos
- **Configurable** via `appsettings.json`:
```json
{
  "ColaOffline": {
    "IntervaloProcesoSegundos": 5,
    "MaxIntentos": 5,
    "BaseDelaySegundos": 1,
    "MaxDelaySegundos": 30
  }
}
```

---

## 8. Clasificación de Errores

| Tipo | Ejemplo | Acción |
|------|---------|--------|
| **Conexión** | Firebird no responde, timeout | Reintentar con backoff |
| **Dato** | Violación de constraint NOT NULL | Marcar FALLIDO (dato inválido) |
| **Duplicado** | Venta ya existe | Marcar COMPLETADO (idempotente) |
| **Desconocido** | Error inesperado | Reintentar, luego FALLIDO |

### Ejemplo de Clasificación

```csharp
try
{
    await InsertarEnFirebird(operacion);
    operacion.Estado = EstadoOperacion.COMPLETADO;
}
catch (FbException ex) when (ex.ErrorCode == -803) // Violación de clave duplicada
{
    operacion.Estado = EstadoOperacion.COMPLETADO; // Ya existe, idempotente
}
catch (FbException ex) when (ex.ErrorCode == -551) // Sin permisos
{
    operacion.Estado = EstadoOperacion.FALLIDO; // No se puede resolver con retry
}
catch (Exception)
{
    operacion.Intentos++;
    if (operacion.Intentos >= operacion.MaxIntentos)
        operacion.Estado = EstadoOperacion.FALLIDO;
    else
        operacion.SiguienteReintento = CalcularSiguienteReintento(operacion.Intentos);
}
```

---

## 9. Archivos a Crear (Sprint 2)

### Estructura

```
Rutx.Sincronizador/
├── Models/
│   ├── ColaOperacion.cs              → Modelo de entidad
│   ├── EstadoOperacion.cs            → Enum de estados
│   └── TipoOperacion.cs              → Enum de tipos
├── Data/
│   ├── IColaOfflineRepository.cs     → Interfaz del repositorio
│   └── ColaOfflineRepository.cs      → Implementación SQLite
├── Services/
│   ├── IColaOfflineService.cs        → Interfaz del servicio
│   ├── ColaOfflineService.cs         → Lógica de negocio
│   └── BackgroundSyncService.cs      → Servicio en segundo plano
├── Controllers/
│   └── ColaController.cs             → Endpoints de la cola
└── Middleware/
    └── ErrorHandlingMiddleware.cs    → Manejo global de errores
```

### Dependencias NuGet (Sprint 2)

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
```

---

## 10. Configuración (appsettings.json)

```json
{
  "ColaOffline": {
    "IntervaloProcesoSegundos": 5,
    "MaxIntentos": 5,
    "BaseDelaySegundos": 1,
    "MaxDelaySegundos": 30,
    "RutaSqlite": "Data/cola_offline.db"
  },
  "ConnectionStrings": {
    "FirebirdConnection": "User=SYSDBA;Password=<tu_password>;Database=CHOCOLATES.fdb;DataSource=localhost;Port=3050;"
  },
  "MicrosipSettings": {
    "DefaultMonedaId": 1,
    "DefaultCondPagoId": 1
  }
}
```

---

## 11. Métricas y Monitoreo

### Contadores a Exponer

| Métrica | Descripción |
|---------|-------------|
| `cola_operaciones_pendientes` | Operaciones esperando ser procesadas |
| `cola_operaciones_procesando` | Operaciones siendo procesadas ahora |
| `cola_operaciones_completadas` | Total completadas exitosamente |
| `cola_operaciones_fallidas` | Total en FALLIDO permanente |
| `cola_tiempo_proceso_ms` | Tiempo promedio de procesamiento |
| `cola_reintentos_total` | Total de reintentos realizados |

### Health Check Endpoint

```
GET /health/cola
```

**Response (200):**
```json
{
  "status": "Healthy",
  "pendientes": 5,
  "procesando": 1,
  "fallidas": 0,
  "ultimo_proceso": "2026-06-26T10:30:00Z"
}
```
