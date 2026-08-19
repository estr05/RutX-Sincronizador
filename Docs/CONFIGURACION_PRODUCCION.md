# Configuración de Producción — RutX Sincronizador

## Regla fundamental

**Ningún valor sensible debe vivir en `appsettings.json` ni en el repositorio.**
Todos los secretos y rutas de entorno se inyectan mediante variables de entorno del Sistema Operativo.

---

## 1. Topología de red recomendada

```
Internet / Web-RutX (servidor remoto)
    │
    │   HTTPS con TLS válido
    ▼
Reverse Proxy local (IIS, nginx, Caddy, etc.)
    │   Publica SOLO: /api/v2/web/*
    │   Bloquea:      /admin  /api/v2/admin/*
    ▼
http://localhost:5047  ◄── Sincronizador RutX
```

El Sincronizador **no debe escuchar directamente en una interfaz pública** (`0.0.0.0`).
El reverse proxy es responsable de TLS, cabeceras de seguridad y filtrado de rutas.

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

> [!CAUTION]
> Si `Jwt__Key` o `ConnectionStrings__FirebirdConnection` contienen los placeholders
> `CHANGE_ME_*`, el Sincronizador **no arrancará** en entorno `Production`.

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
