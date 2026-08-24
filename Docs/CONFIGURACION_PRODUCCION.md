# Configuración de Producción — RutX Sincronizador

## Regla fundamental

**Ningún valor sensible debe vivir en `appsettings.json` ni en el repositorio.**
Todos los secretos y rutas de entorno se inyectan mediante variables de entorno del Sistema Operativo.

---

## 1. Topología de red recomendada

```
App móvil (producción)
    │ HTTPS
    ▼
Cloudflare Edge ◀── túnel saliente (outbound-only) ── cloudflared (servicio Windows)
        │                                              │ HTTP plano solo loopback
        │ allowlist ingress; catch-all 404             ▼
        └──────────────────────────────────▶ 127.0.0.1:5048  (listener ReverseProxy)

Administración local (panel /admin, /api/v2/admin/*): SOLO 127.0.0.1:5047

Desarrollo (transitorio):
    App móvil ── HTTP LAN/Tailscale ──▶ 0.0.0.0:5047   (ListenAnyIP solo en Development)

Portal Web-RutX (opcional, si se publica):
Internet ──HTTPS──▶ Reverse Proxy local (IIS/nginx/Caddy)
        publica SOLO /api/v2/web/*  ──▶ http://localhost:5047
```

El Sincronizador **no debe escuchar directamente en una interfaz pública**
en Production (`KestrelTopology`: Production = loopback estricto en 5047;
5048 loopback solo para cloudflared). Ver `Deploy/Cloudflare/README.md`
para el procedimiento completo del túnel y la delegación del dominio
(aunque el registrador sea GoDaddy).

---

## 2. Variables de entorno requeridas en producción

Define estas variables en el perfil de usuario del servicio Windows o en el
bloque `Environment` del archivo de servicio (SCM / NSSM / Task Scheduler):

| Variable de entorno | Sección appsettings | Descripción |
|---|---|---|
| `ConnectionStrings__FirebirdConnection` | `ConnectionStrings:FirebirdConnection` | Cadena completa con `Password=` real |
| `Jwt__Key` | `Jwt:Key` | Clave HMAC-SHA256, mínimo 32 caracteres, generada con CSPRNG |
| `Jwt__Issuer` | `Jwt:Issuer` | Identificador del emisor (default: `RutxSincronizador`) |
| `Jwt__Audience` | `Jwt:Audience` | Audiencia del token (default: `RutxApps`) |
| `WebAuth__AdminUsername` | `WebAuth:AdminUsername` | Usuario administrador inicial del portal |
| `WebAuth__AdminPassword` | `WebAuth:AdminPassword` | Contraseña del administrador inicial (hash PBKDF2 interno) |
| `WebSqlite__Ruta` | `WebSqlite:Ruta` | Ruta de `RUTX_COMPLEMENTARIA.db` (default: `C:\Microsip Extras\RUTX_COMPLEMENTARIA.db`) |
| `ColaOffline__RutaSqlite` | `ColaOffline:RutaSqlite` | Misma ruta que `WebSqlite:Ruta` |
| `Storage__FotosPath` | `Storage:FotosPath` | Carpeta de fotos (default: `C:\Microsip Extras\Fotos\NoVentas`) |
| `ASPNETCORE_ENVIRONMENT` | — | Debe ser `Production` en producción (activa validaciones estrictas) |
| `Network__ExternalApiEnabled` | `Network:ExternalApiEnabled` | `false` por defecto (versionado). Solo `true` cuando se activa el túnel |
| `Network__ExternalApiMode` | `Network:ExternalApiMode` | `ReverseProxy` para Cloudflare Tunnel (listener loopback 5048) |
| `Network__ExternalPort` | `Network:ExternalPort` | Puerto del listener ReverseProxy (default 5048) |
| `AllowedHosts` | `AllowedHosts` | OBLIGATORIO con API externa activa: `sync.<cliente>.com;localhost;127.0.0.1` |
| `Cloudflare__PublicHostname` | `Cloudflare:PublicHostname` | OBLIGATORIO con API externa activa; debe estar incluido en `AllowedHosts` |

> [!CAUTION]
> Si `Jwt__Key` o `ConnectionStrings__FirebirdConnection` contienen los placeholders
> `CHANGE_ME_*`, el Sincronizador **no arrancará** en entorno `Production`.

### Activación del túnel en el equipo demo

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:Network__ExternalApiEnabled = "true"
$env:Network__ExternalApiMode = "ReverseProxy"
$env:Network__ExternalPort = "5048"
$env:AllowedHosts = "sync.<cliente>.com;localhost;127.0.0.1"
$env:Cloudflare__PublicHostname = "sync.<cliente>.com"
# + secretos reales rotados (Jwt__Key, WebAuth__*, ConnectionStrings__*)
```

Si falta `AllowedHosts` válido o `Cloudflare__PublicHostname`, el arranque
**aborta** (`ProductionConfigurationValidator`).

---

## 3. Generación segura de `Jwt__Key`

```powershell
# PowerShell — genera 48 bytes aleatorios (384 bits) en Base64
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
```

Guardar el resultado como variable de entorno del sistema. No compartirlo por correo ni Slack.

---

## 4. Rotación de secretos

### JWT Key
1. Generar nueva clave.
2. Actualizar la variable de entorno en el servidor.
3. Reiniciar el servicio Windows (`RutxSincronizador`).
4. **Todos los tokens activos quedan invalidados** — los usuarios deberán hacer login de nuevo.

### Contraseña Firebird
1. Cambiar la contraseña en Firebird/Microsip primero.
2. Actualizar `ConnectionStrings__FirebirdConnection` en la variable de entorno.
3. Reiniciar el servicio.

---

## 5. Feature flags

Los módulos no implementados están bloqueados por defecto (`false`).
Para activar un módulo cuando esté listo, establecer la variable de entorno:

| Variable | Descripción |
|---|---|
| `WebFeatures__Dashboard=true` | Activa el tablero de KPIs |
| `WebFeatures__RouteMonitor=true` | Activa el monitoreo de rutas |
| `WebFeatures__Reports=true` | Activa los reportes de ventas |
| `WebFeatures__RouteProfitability=true` | Activa rentabilidad por ruta |

En Web-RutX (Laravel), el flag correspondiente controla la experiencia visual:

| Variable `.env` Web-RutX | Descripción |
|---|---|
| `API_WEB_DASHBOARD_ENABLED=true` | Activa UI del tablero |
| `API_WEB_ROUTE_MONITOR_ENABLED=true` | Activa UI de monitoreo |
| `API_WEB_REPORTS_ENABLED=true` | Activa UI de reportes |

> [!IMPORTANT]
> El backend (Sincronizador) es la **autoridad final**. Aunque el flag de Web-RutX
> esté en `true`, el backend responderá `HTTP 501 FEATURE_NOT_READY` si su propio
> flag sigue en `false`.

---

## 6. Verificación post-despliegue

```powershell
# Verificar que el servicio arrancó correctamente
Get-Service -Name "RutxSincronizador" | Select-Object Status, StartType

# Verificar health endpoint (local)
Invoke-RestMethod http://localhost:5047/health

# Verificar que /admin NO está accesible desde red (debe dar 404)
# Ejecutar desde máquina externa:
# curl https://tu-dominio.com/admin  → esperar 404
```

---

## 7. Archivos que NO deben estar en el repositorio

- `appsettings.Production.json` con valores reales
- `.env` del servidor con secretos
- `instalacion.json` de instancias de clientes
- `*.bak` de bases de datos
- Certificados TLS (`.pfx`, `.pem`, `.key`)

---

## 7. Credenciales locales fuera de Git (desarrollo/demo)

Los archivos versionados solo contienen placeholders `CHANGE_ME_*`. Las
credenciales reales de desarrollo/demo viven en `appsettings.Local.json`
(copia de `appsettings.Local.example.json`, excluido por `.gitignore`).

**Precedencia garantizada** (verificada en
`Rutx.Sincronizador.Tests/Unit/ConfiguracionPrecedenciaTests.cs`):

```
appsettings*.json  <  appsettings.Local.json  <  variables de entorno / CLI
```

Program.cs re-registra los proveedores de entorno y l�nea de comandos
DESPU�S del archivo local para que conserven la �ltima palabra.

### Checklist de verificaci�n (orden vinculante)

1. Crear usuario Firebird dedicado `RUTX_SYNC` seg�n
   `Docs/FIREBIRD_GRANTS_RUTX_SYNC.md` (probar primero en COPIA; NO usar
   SYSDBA/masterkey).
2. Copiar plantilla ? `appsettings.Local.json` con valores reales rotados.
3. `dotnet run` en Development ? arranque OK.
4. Login + `GET /api/v1/routes/sync` desde la app por **LAN** ? datos
   reales (Firebird consultable con RUTX_SYNC).
5. Repetir por **Tailscale**.
6. SOLO entonces activar Cloudflare Tunnel (`Deploy/Cloudflare/README.md`).

Si el paso 4 falla por permisos, corregir los GRANTs en la copia y repetir;
no regresar a SYSDBA.

---

## 8. Secretos e historial p�blico

Ambos repositorios son p�blicos: la rotaci�n neutraliza los valores viejos,
pero siguen visibles en el historial. La purga coordinada es OBLIGATORIA y
precede al primer push normal de este feature � secuencia completa en
`Purga-Historial.md`.

---

## 9. Secuencia de aceptacion E2E (equipo demo)

Orden obligatorio; no se avanza de fase si la anterior falla:

1. Arranque local del sincronizador en modo consola (`dotnet run` o exe).
2. Login por LAN desde un telefono en el mismo WiFi (`http://<IP-LAN>:5047`).
3. Descarga matutina completa por LAN (catalogos sin errores).
4. Sincronizacion por Tailscale (WiFi apagado en el telefono).
5. Activar ReverseProxy: `Network__ExternalApiEnabled=true` +
   `Network__ExternalApiMode=ReverseProxy` => listener `127.0.0.1:5048`.
6. Levantar cloudflared con el config materializado
   (`cloudflared tunnel run <nombre>`) y verificar logs "Registered".
7. Health publico: `curl https://<hostname>/health` desde DATOS MOVILES
   (WiFi apagado) => 200.
8. Login por datos moviles contra `https://<hostname>` (JWT emitido).
9. Descarga matutina completa por tunel.
10. Venta y cobranza de punta a punta por tunel (DOCTOS_PV/DOCTOS_CC en Firebird).
11. Modo avion => estado offline visible; reactivar senal => drenaje de las
    ventas/cobranzas pendientes y cierre de ruta sin duplicados.

Cualquier paso en rojo detiene el despliegue y se registra evidencia.
