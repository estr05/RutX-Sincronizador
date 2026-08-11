# RUTX Sincronizador

API REST en **C# (.NET 10)** que actúa como **middleware entre la app móvil RUTX (Flutter)
y el ERP Microsip (Firebird)**. Recibe las operaciones del vendedor (login, ventas,
no-ventas, cobranza, clientes, créditos, cierre de ruta) y las escribe **directamente
en la base de datos de Microsip** usando el esquema de **Punto de Venta (PV)**, respetando
sus triggers, generadores y constraints.

```
┌─────────────────┐   HTTPS/JSON    ┌───────────────────┐   Firebird (SQL)   ┌──────────────────┐
│  App Móvil RUTX │ ──────────────► │  Sincronizador    │ ─────────────────► │  Microsip ERP    │
│  (Flutter,      │  JWT Bearer     │  (.NET 10, :5047) │    DOCTOS_PV y      │  ([EMPRESA].FDB)   │
│  offline-first) │                 │                   │    catálogos        │  Punto de Venta  │
└─────────────────┘                 └───────────────────┘                     └──────────────────┘
```

Ambos proyectos son **dos caras de la misma moneda** y se desarrollan de la mano:

| Proyecto | Repositorio | Rol |
|---|---|---|
| **Sincronizador** (este) | `sincronizador_rutx` | Puente de escritura hacia la BD de Microsip |
| **App móvil RUTX** | `appmovil_rutx` | Terminal del vendedor, con operación offline |

---

## 🧩 Dependencia del ERP Microsip (Firebird)

El sincronizador **no tiene base de datos propia para el negocio**: opera 100% contra la
BD de Microsip. Por eso una BD compatible es un requisito **crítico**.

### Login nativo Firebird (sin AGENTES)
El login **ya no usa la tabla `AGENTES`** (flujo retirado). Autentica con **credenciales
nativas de Firebird** y resuelve la identidad de ruta del usuario:

```
usuario (Firebird) → VENDEDORES.NOMBRE = usuario → CAJEROS.USUARIO = usuario
                   → CAJAS_CAJEROS (acceso 'A'/'O') → CAJAS.ALMACEN_ID
```

Si un vendedor no tiene cajero por USUARIO o cajero sin caja asignada, el login responde `403`.

### Tablas núcleo que toca
`VENDEDORES`, `CAJEROS`, `CAJAS`, `CAJAS_CAJEROS`, `CLIENTES`, `DIRS_CLIENTES`,
`ARTICULOS`, `CLAVES_ARTICULOS`, `PRECIOS_ARTICULOS`, `PRECIOS_EMPRESA`, `IMPUESTOS`,
`IMPUESTOS_ARTICULOS`, `FORMAS_COBRO`, `FOLIOS_CAJAS`, `SALDOS_IN`, `SALDOS_CC`,
`DOCTOS_PV`, `DOCTOS_PV_DET`, `DOCTOS_PV_COBROS`, `DOCTOS_PV_LIGAS`,
`IMPUESTOS_DOCTOS_PV`, `IMPUESTOS_DOCTOS_PV_DET`, `DOCTOS_CC`, `DOCTOS_ENTRE_SIS`,
`VENCIMIENTOS_CARGOS_CC`, `MOVTOS_EFVO_CAJA`, `MOVTOS_CAJAS`, `SUCURSALES`, `RFCS_LCO`,
`MONEDAS`, `CONDICIONES_PAGO`, `ALMACENES`, `CONCEPTOS_CC`.

### Triggers críticos (NO se crean ni modifican)
`DOCTOS_PV_BEFINS`, `DOCTOS_PV_DET_BEFINS`, `DOCTOS_PV_COBROS_BEFINS`,
`DOCTOS_PV_COBROS_AFTINS_0`, `DOCTOS_PV_AFTUPD_0`, `DOCTOS_PV_BEFUPD_0`,
`DOCTOS_PV_LIGAS_BEFINS`, `DOCTOS_CC_BEFINS` + generador `ID_DOCTOS`.
La FK `CAJEROS_A_DOCTOS_PV` la usa el `FkResolverService`.

### IDs de configuración (`MicrosipSettings`)
El sincronizador usa IDs por defecto que **deben existir en la BD asignada**; si la BD
tiene otros, se reasignan en `appsettings.json`:

| Clave | Default | Nota |
|---|---|---|
| `DefaultMonedaId` | 1 | Moneda base |
| `DefaultCondPagoId` | 1 (Dev: 2422) | Condición de pago "Contado" |
| `DefaultImpuestoId` | 622 | IVA 16% |
| `DefaultSucursalId` | 4274 | Sucursal por defecto |
| `DefaultAlmacenId` | 19 | Almacén por defecto |
| `DefaultPrecioEmpresaId` | 42 | Lista de precios |
| `DefaultFormaCobroId` | 67 | Efectivo |
| `CreditFormaCobroIds` | [71, 703, 2205] | Formas de cobro a crédito |
| `DefaultConceptoCobroId` | 11 | Concepto de abono CxC |
| `DefaultCajeroId` | 2419 | Solo fallback |

> ⚠️ `appsettings.Development.json` **sobrescribe** valores al correr con
> `ASPNETCORE_ENVIRONMENT=Development` (lo hace `dotnet run` por defecto). Verifica qué
> entorno usarás antes de juzgar un ID como "faltante".

### Auditoría de compatibilidad
La carpeta **`Integracion/`** contiene el **super prompt** y el **script de auditoría**
(`auditar_compatibilidad.py`, 100% solo-lectura) para validar que una BD de Microsip
tiene todo lo que este sincronizador necesita:

```bash
# desde la carpeta del repo clonado
python "Integracion/auditar_compatibilidad.py" "C:\ruta\TU_BD.fdb" --appsettings appsettings.json
```

El script verifica tablas/columnas, triggers, FK, IDs de `appsettings`, datos mínimos y
la cadena de login (VENDEDOR→CAJERO→CAJA). Ver `Integracion/PROMPT_AUDITORIA.md` para el
flujo completo de auditoría (FASE 1 solo lectura, FASE 2 solo con aprobación).

---

## 📡 Endpoints

### Autenticación (JWT)
- `POST /api/auth/login` — login con usuario/password **de Firebird** → `vendedor_id`, `caja_id`, token
- `GET  /api/auth/me` — identidad del token

### Flujo de ruta
- `GET  /api/v1/routes/sync` — sincronización matutina (clientes, productos, formas de cobro, emisor, sucursal)
- `GET  /api/v1/routes/summary` — resumen diario / pre-cierre
- `POST /api/v1/routes/close` — cierre de ruta
- `GET  /api/v1/routes/debug/doctos-pv` · `DELETE /api/v1/routes/debug/doctos-pv` — diagnóstico (debug)

### Ventas (Punto de Venta)
- `POST /api/v1/pv/ventas` — registrar venta (folio consecutivo + auto-aplicación)
- `POST /api/v1/pv/noventa` — registrar no-venta
- `POST /api/v1/pv/ventas/{id}/aplicar` · `.../cancelar` — aplicar/cancelar
- `GET  /api/v1/pv/ventas/{id}` — ticket completo (JOIN de 5+ tablas)

### Otros módulos
- `POST /api/v1/cobranza/insert` — cobranza (pago de ventas a crédito → DOCTOS_CC)
- `GET  /api/v1/credito/pedidos` · `GET /api/v1/credito/clientes/{id}/documentos` — CxC
- `POST /api/v1/clientes` — alta de cliente
- `POST /api/v1/folios/reparar` — reparación de contadores de folios
- `GET  /api/v1/messages` · `POST /api/v1/messages` — mensajería oficina
- `GET  /api/v1/queue` · `GET /api/v1/queue/failed` · `POST /api/v1/queue/retry/{id}` — cola offline
- `GET  /api/dbcompare/*` — utilidades de comparación (diagnóstico)
- `GET  /health` — health check para la app

### Folios seguros
La reserva de folios es **atómica** (`UPDATE ... RETURNING` en `FOLIOS_CAJAS`) con
candado por caja (`FolioLockService`), reintentos (`FirebirdRetryPolicy`) y
**auto-creación del bloque de folios** si la caja no tiene (`AutoCrearFolios=true`).

---

## ⚙️ Configuración

`appsettings.json`:

```json
{
  "ConnectionStrings": {
    "FirebirdConnection": "User=(...);Password=(...);Database=C:\\Microsip datos\\TU_BD.fdb;DataSource=localhost;Port=3050;Dialect=3;Pooling=true;",
    "SQLiteConnection": "Data Source=Data\\cola_offline.db"
  },
  "Jwt": { "Key": "...", "Issuer": "RutxSincronizador", "Audience": "RutxApps" },
  "MicrosipSettings": { "...": "ver tabla de IDs arriba" }
}
```

- Puerto por defecto: **5047** (configurable por `ASPNETCORE_URLS`).
- La **cola offline** del servidor usa SQLite (`Data/cola_offline.db`) — archivo local,
  excluido de Git.

---

## 🖥️ Interfaz de administración

El sincronizador incluye una **interfaz de administración** pensada para configurar y
supervisar la instalación en cada cliente, **sin tocar archivos a mano** y **sin afectar
el contrato móvil** (`/api/v1/*` queda congelado).

```
┌───────────────────────────────┐
│  Rutx.Sincronizador.Admin.exe │ ← Launcher WinForms (uso diario)
│  [▶ Iniciar] [⏹ Detener]      │    botones arriba + logs verdes al centro
│  [⚙ Conf (web)]               │
├───────────────────────────────┤
│  Sincronizador (:5047)        │
│  ├── /api/v1/*  → app móvil   │ ← contrato congelado
│  └── /admin     → panel web   │ ← config bajo demanda
└───────────────────────────────┘
```

### 🪟 Launcher Windows (`Rutx.Sincronizador.Admin.exe`)

Proyecto **WinForms** (`Rutx.Sincronizador.Admin/`) que **controla** el sincronizador
como proceso hijo — no lo modifica:

- **▶ Iniciar** — arranca `Rutx.Sincronizador.exe` y captura su consola.
- **⏹ Detener** — detiene el proceso (el lock anti-huérfanos auto-limpia residuos).
- **⚙ Conf (web)** — abre el panel web `http://localhost:5047/admin`.
- **Logs en verde** — INFO=verde, WARN=amarillo, ERROR=rojo (consola Windows).

```bash
# publicar el launcher (junto al exe del sync)
dotnet publish Rutx.Sincronizador.Admin/Rutx.Sincronizador.Admin.csproj -c Release
```

### 🌐 Panel web de configuración (`/admin`)

Servido por el propio sincronizador (`wwwroot/admin.html` + `Controllers/Web/AdminController.cs`).
Se abre bajo demanda con el botón **Conf (web)** o con **`AbrirAdmin.bat`**.

| Ruta | Función |
|---|---|
| `GET  /api/v2/admin/config` | Lee `appsettings.json` (BD, IDs, JWT, cola) |
| `POST /api/v2/admin/config` | Guarda con **respaldo `.bak`** automático (reloadOnChange aplica en caliente) |
| `GET  /api/v2/admin/vendedores` | Lista vendedores de Microsip para el desplegable |
| `POST /api/v2/admin/sync-matutino` | Ejecuta la sincronización matutina (reutiliza `RouteService`) |

> ⚠️ Seguridad: las rutas `/api/v2/admin/*` **no requieren autenticación** porque están
> pensadas para uso local (localhost). No exponer el puerto 5047 a internet sin agregar
> un mecanismo de autenticación.

### 🚀 Cómo usarlo

#### En desarrollo (tu PC)

```bash
# 1) Arrancar el sincronizador (opcional: el launcher lo hace solo)
dotnet run --project Rutx.Sincronizador.csproj

# 2) Arrancar el launcher WinForms (botones + logs verdes)
dotnet run --project Rutx.Sincronizador.Admin

# 3) Acceso rápido al panel web (otra opción: botón ⚙ Conf (web) del launcher)
./AbrirAdmin.bat          # o abre http://localhost:5047/admin en el navegador
```

Flujo diario en la ventana del launcher:

1. **▶ Iniciar** — levanta la API en `:5047` (los logs aparecen en verde en pantalla).
2. Trabaja normal: la app móvil conecta a `:5047` como siempre (contrato intacto).
3. **⚙ Conf (web)** — solo cuando necesites cambiar BD, IDs o ejecutar un sync matutino manual.
4. **⏹ Detener** — cierra la API cuando termines la jornada.

#### En producción (PC del cliente)

```bash
# Publicar ambos proyectos (el launcher + el sync) en la misma carpeta
mkdir publicacion
# 1) El sincronizador (API + panel web /admin)
dotnet publish Rutx.Sincronizador.csproj -c Release -o publicacion
# 2) El launcher WinForms (debe quedar JUNTO al exe del sync)
dotnet publish Rutx.Sincronizador.Admin/Rutx.Sincronizador.Admin.csproj -c Release -o publicacion

# Resultado:
# publicacion/
# ├── Rutx.Sincronizador.exe        ← API (lo controla el launcher)
# ├── Rutx.Sincronizador.Admin.exe  ← Launcher: doble clic y listo
# ├── appsettings.json              ← config editable desde el panel web
# └── wwwroot/admin.html            ← panel web (incluido)
```

El cliente solo hace **doble clic en `Rutx.Sincronizador.Admin.exe`**:
Inicia, ve los logs verdes y abre el panel web cuando necesite configurar.

---

## 🗂️ Estructura del proyecto

```
sincronizador_rutx/
├── Integracion/                    ← Prompt + script de auditoría (viajan juntos)
│   ├── PROMPT_AUDITORIA.md         ← Super prompt (FASE 1 solo lectura / FASE 2 aprobada)
│   └── auditar_compatibilidad.py   ← Auditor 100% solo-lectura de la BD de Microsip
├── Controllers/                    ← Endpoints HTTP por área (ver Controllers/README.md)
│   ├── Movil/                      ← CONTRATO MÓVIL congelado: endpoints que usa la app RUTX
│   ├── Compartidos/                ← Lógica útil para móvil y web (ej. alta de clientes)
│   ├── Admin/                      ← Mantenimiento y diagnóstico (folios, cola, dbcompare)
│   └── Web/                        ← Panel de administración (/api/v2/admin/*) y web futura
├── wwwroot/                        ← Panel web de configuración (admin.html)
├── Rutx.Sincronizador.Admin/       ← Launcher WinForms (Iniciar/Detener/Conf + logs)
├── AbrirAdmin.bat                  ← Acceso rápido al panel web (/admin)
├── Services/                       ← Lógica de negocio (VentaServicePv, RouteService,
│                                      FirebirdAuthService, CobranzaService, CreditoService,
│                                      FolioService, ColaOfflineService, FkResolverService...)
├── Models/                         ← DTOs y modelos JSON

├── Data/                           ← Repositorio de la cola offline SQLite
├── Middleware/                     ← Manejo de errores
├── Properties/launchSettings.json  ← Perfiles de arranque (Development, puertos)
├── Docs/                           ← Documentación técnica (contratos, diseño cola offline)
├── Rutx.Sincronizador.Tests/       ← Tests unitarios e integración
├── Program.cs                      ← Punto de entrada + lock anti-duplicados
├── Rutx.Sincronizador.csproj       ← Proyecto .NET 10
├── rutx-sincronizador.sln          ← Solución
├── appsettings.json                ← Configuración (BD, JWT, IDs por defecto)
├── appsettings.Development.json    ← Sobrescrituras de Development
├── Rutx.Sincronizador.http         ← Ejemplos de peticiones (VS Code)
└── .gitignore
```

---

## 🚀 Setup local

1. Clona el repo (incluye `Integracion/` y `auditar_compatibilidad.py`).
2. Requisitos: **.NET 10 SDK**, Python 3 + `pip install fdb` (para la auditoría).
3. Configura `appsettings.json` con la ruta de tu BD `.fdb` de Microsip.
4. (Opcional pero recomendado) Audita la BD:
   ```bash
   python "Integracion/auditar_compatibilidad.py" "C:\ruta\TU_BD.fdb"
   ```
5. `dotnet restore` y `dotnet run` → escucha en `http://0.0.0.0:5047`.
6. Health check: `GET http://localhost:5047/health`.

### Lock anti-duplicados
El proceso usa `%TEMP%\RutxSincronizador_5047.lock`. Si queda huérfano por un cierre
abrupto, se detecta y elimina automáticamente al arrancar (no hay que borrarlo a mano).

---

## 🧪 Tests

```bash
dotnet test
```
Cubre servicios (ventas PV, cobranza, crédito, folios), repositorios y flujos de cola.

---

## 🔗 Relación con la app móvil RUTX

La app móvil (`appmovil_rutx`) es el **terminal offline-first** del vendedor: guarda las
operaciones en SQLite local y las sube aquí cuando hay conexión (`POST /api/v1/pv/ventas`,
`POST /api/v1/cobranza/insert`, etc.). Este sincronizador es la **única vía de escritura
hacia la BD de Microsip**. Apunta a este servidor por el puerto **5047** (`ApiConstants`).

---

## Licencia
Propietario — Teknologix / RUTX
