# Instalación RutX en PC de cliente — orden exacto

> Placeholders: `<HOSTNAME>` = `api.<cliente>.com`, `<RUTA_COYATOC.FDB>`,
> `<PASS_RUTX_SYNC>` (aleatoria ≥20 chars), `<TOKEN_TUNNEL>`.
> Nunca escribir valores reales en Git: viven solo en la PC del cliente
> (`appsettings.Local.json`) y en el gestor de secretos.

## 0. Prerrequisitos en la PC del cliente
1. Microsip instalado y operativo con su Firebird local (puerto 3050).
2. `.NET 10 Runtime` (o SDK) x64 instalado.
3. Salida HTTPS 443 permitida hacia Cloudflare (`*.cfargotunnel.com`).

## 1. Firebird/Microsip → usuario del sincronizador (FaseB_01)
4. Con `isql` conectado a `<RUTA_COYATOC.FDB>` como SYSDBA (password por
   prompt/ISC_PASSWORD, jamás en archivos):
   ejecutar `Deploy\Firebird\FaseB_01_crear_usuario_y_grants.sql`
   — crea `RUTX_SYNC` (sin rol admin) y sus grants mínimos.
5. Validar con `Deploy\Firebird\FaseB_03_matriz_permisos.ps1`
   (SELECT permitidos / escrituras denegadas sin grants extra).
6. Guardar `<PASS_RUTX_SYNC>` en el gestor de secretos; no se pide de nuevo.

## 2. Wizard → servicio Windows
7. Copiar carpeta de publicación Release (`Rutx.Sincronizador.Admin.exe`,
   `Rutx.Sincronizador.exe`, DLLs) a `C:\ProgramData\RUTX\Sincronizador`.
8. Ejecutar `Rutx.Sincronizador.Admin.exe` → wizard:
   - valida puerto **5047 libre** (administración);
   - pide ruta de BD + usuario `RUTX_SYNC`/`<PASS_RUTX_SYNC>`;
   - pide la **Contraseña Panel Web** (user `admin`, **mín. 8 caracteres**;
     se pide cambio al primer acceso; va solo en `appsettings.Local.json`);
   - opcional: **Habilitar acceso remoto público (Cloudflare Tunnel)** + un
     **Hostname público FQDN** real (ej. `sync.cliente.com`; sin IPs ni
     `sync.ejemplo.com`); activa el listener loopback `127.0.0.1:5048`;
   - genera `appsettings.Local.json` (**fuera de Git**, junto al exe);
   - instala/inicia el servicio Windows `RutxSincronizador`;
   - verifica `/health`.

## 3. Variables Production del servicio
9. En el servicio (`HKLM\SYSTEM\CurrentControlSet\Services\RutxSincronizador`
   → `Environment`, REG_MULTI_SZ):
   ```
   ASPNETCORE_ENVIRONMENT=Production
   Network__ExternalApiEnabled=true
   Network__ExternalApiMode=ReverseProxy
   Network__ExternalPort=5048
   Cloudflare__PublicHostname=<HOSTNAME>
   AllowedHosts=<HOSTNAME>;localhost;127.0.0.1
   ```
10. Reiniciar servicio y comprobar:
    - `http://127.0.0.1:5047/health` → 200 (admin, loopback);
    - `http://127.0.0.1:5048/health` → 200 (túnel, loopback);
    - desde otra PC de la red **no** debe responder ninguno de los dos.

## 4. Túnel Cloudflare → hostname
11. En el dashboard Cloudflare del cliente: Zero Trust → Networks → Tunnels
    → Create tunnel (copiar `<TOKEN_TUNNEL>`).
12. En la PC cliente: instalar `cloudflared` y registrar servicio:
    `cloudflared service install <TOKEN_TUNNEL>`
    (o usar `Deploy\Cloudflare\Prepare-CloudflareTunnel.ps1`).
13. Public Hostname: `<HOSTNAME>` → Service URL **`http://127.0.0.1:5048`**
    (HTTP; TLS lo termina Cloudflare). El 5047 se reserva exclusivamente
    para el panel/administración local (loopback) y nunca se expone.
14. Verificar: `https://<HOSTNAME>/health` → 200.

## 5. APK del cliente
15. Generar keystore del cliente (una vez, fuera del repo):
    ```
    keytool -genkeypair -v -keystore C:\Users\<user>\keystores\rutx-<cliente>-upload.jks
            -alias rutx_upload -keyalg RSA -keysize 2048 -validity 10000
    ```
16. Copiar `android\key.properties.example` → `android\key.properties`
    (fuera de Git) con alias/rutas/passwords reales.
17. Compilar:
    ```
    flutter build apk --release --dart-define=API_BASE_URL=https://<HOSTNAME>
    ```
    La URL queda fijada en compilación (HTTPS obligatorio; sin fallback
    LAN/Tailscale). No requiere pantalla de configuración.
18. Instalar APK en teléfono del vendedor y validar login + venta +
    cobranza marcados como prueba (`RUTX-E2E`/datos de demostración).

## Rollback
- Servicio: detener `RutxSincronizador`; el wizard no modifica la BD.
- Túnel: `cloudflared service uninstall` y borrar el Public Hostname.
- La app sin `API_BASE_URL` vuelve al modo desarrollo (no distribuir así).
