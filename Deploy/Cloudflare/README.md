# Cloudflare Tunnel — Sincronizador RutX (despliegue en el equipo Windows)

Publica la API móvil del sincronizador mediante un hostname HTTPS de
Cloudflare **sin abrir ningún puerto de entrada**. El tráfico público queda
limitado a la allowlist de `config.yml.example`; todo lo demás responde `404`
en el edge.

```
Internet ──HTTPS──▶ Cloudflare Edge ◀──túnel salente (outbound-only)── cloudflared (servicio Windows)
                                                        │ HTTP plano, solo loopback
                                                        ▼
                                            127.0.0.1:5048  (listener ReverseProxy de Kestrel)
                                            127.0.0.1:5047  (administración local; NUNCA expuesto)
```

---

## 1. Requisito previo: dominio DELEGADO a Cloudflare

El hostname del túnel debe vivir dentro de una zona activa en Cloudflare.
**Que el dominio esté comprado en GoDaddy NO es suficiente**: si los
nameservers siguen apuntando a GoDaddy, el CNAME del túnel no se integra con
el edge y el hostname no resolverá contra el túnel.

Pasos (GoDaddy → Cloudflare):

1. En el dashboard de Cloudflare: *Add a site* → escribir el dominio → elegir
   plan → Cloudflare asigna **2 nameservers**, p. ej.
   `ana.ns.cloudflare.com` y `bob.ns.cloudflare.com`.
2. En GoDaddy: *Mi cuenta → Dominios → DNS → Nameservers → Change* →
   *Enter my own nameservers (advanced)* → sustituir los NS de GoDaddy por los
   dos de Cloudflare → guardar.
3. Esperar la propagación (minutos a 48 h). Verificar:
   ```powershell
   nslookup -type=NS tudominio.com
   ```
   Deben responder los nameservers de Cloudflare, no los de GoDaddy
   (`ns*.domaincontrol.com`).
4. Continuar cuando el estado de la zona en Cloudflare sea *Active*.

> El setup parcial tipo CNAME (*partial/CNAME setup*) es exclusivo del plan
> Enterprise; para este despliegue se usa delegación completa de nameservers.

## 2. Instalar cloudflared en el equipo Windows

```powershell
# Opción winget
winget install --id Cloudflare.cloudflared
# o descargar el .msi oficial desde developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/
```

Verificar: `cloudflared --version`.

## 3. Crear el túnel

```powershell
cloudflared tunnel login                 # abre navegador; genera ~/.cloudflared/cert.pem
cloudflared tunnel create rutx-mobile-production
# Salida esperada:
#   Created tunnel rutx-mobile-production with id <TUNNEL_UUID>
#   Credentials written to C:\Users\<usuario>\.cloudflared\<TUNNEL_UUID>.json
```

`cert.pem` y el JSON de credenciales son secretos: permanecen fuera del repo.

## 4. Materializar configuración (script)

Como administrador:

```powershell
cd <repo>\Deploy\Cloudflare
.\Prepare-CloudflareTunnel.ps1 `
    -TunnelId "<TUNNEL_UUID>" `
    -Hostname "sync.<cliente>.com" `
    -CredentialsJson "C:\Users\<usuario>\.cloudflared\<TUNNEL_UUID>.json"
```

El script: valida GUID/credenciales, escribe
`C:\Windows\System32\config\systemprofile\.cloudflared\config.yml`, copia el
JSON de credenciales ahí, aplica ACLs (Administradores/SYSTEM full; **la cuenta
real del servicio** —resuelta automáticamente de `Win32_Service.StartName`—
solo lectura) y ejecuta `tunnel ingress validate` si `cloudflared` está en PATH.

Si aún no existe el servicio y quieres fijar la cuenta manualmente, añade
`-ServiceAccount "<dominio\cuenta>"`. El script nunca asume NETWORK SERVICE.

## 5. Validar ingress antes de conectar

```powershell
$cfg = "C:\Windows\System32\config\systemprofile\.cloudflared\config.yml"
cloudflared --config=$cfg tunnel ingress validate
cloudflared --config=$cfg tunnel ingress rule https://sync.<cliente>.com/api/v1/routes/sync
cloudflared --config=$cfg tunnel ingress rule https://sync.<cliente>.com/admin   # debe caer al catch-all 404
```

## 6. DNS y servicio

```powershell
# CNAME dentro de la zona Cloudflare: sync.<cliente>.com -> <TUNNEL_UUID>.cfargotunnel.com
cloudflared tunnel route dns rutx-mobile-production sync.<cliente>.com

# Instalar como servicio y arrancar
cloudflared.exe service install
Start-Service cloudflared
Get-CimInstance Win32_Service -Filter "Name='cloudflared'" | Select-Object StartName, State
```

## 7. Configuración del Sincronizador (variables de entorno del equipo)

La config versionada trae `Network.ExternalApiEnabled=false`. La activación se
hace SOLO por entorno:

```powershell
[Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT","Production","Machine")
[Environment]::SetEnvironmentVariable("Network__ExternalApiEnabled","true","Machine")
[Environment]::SetEnvironmentVariable("Network__ExternalApiMode","ReverseProxy","Machine")
[Environment]::SetEnvironmentVariable("Network__ExternalPort","5048","Machine")
[Environment]::SetEnvironmentVariable("AllowedHosts","sync.<cliente>.com;localhost;127.0.0.1","Machine")
[Environment]::SetEnvironmentVariable("Cloudflare__PublicHostname","sync.<cliente>.com","Machine")
# Secretos reales (rotados): ver Docs/CONFIGURACION_PRODUCCION.md
```

El validador de arranque **aborta** si falta AllowedHosts válido o
`Cloudflare__PublicHostname` no aparece exactamente en AllowedHosts.

## 8. Configuración recomendada en el dashboard de Cloudflare

| Ajuste | Valor |
|---|---|
| SSL/TLS mode | `Full` (edge cifra; origen HTTP local por loopback) |
| Cache Rules | **Bypass de caché** para `/api/*` y `/health` (datos de sesión/clientes/cartera) |
| Rate Limiting | Reglas para `POST /api/auth/login` y escrituras (`/api/v1/pv/*`, `/cobranza/insert`). Sin CAPTCHA ni challenges interactivos: Flutter no puede resolverlos |
| Bot Fight Mode | Desactivado para este hostname o excluir rutas `/api/*` |
| Cloudflare Access | **No usar** frente al API móvil (requiere login de navegador); la autenticación es JWT del propio sincronizador |

## 9. Pruebas externas (red móvil / otra red)

```bash
curl https://sync.<cliente>.com/health                        # 200 {"status":"ok",...}
curl -X POST https://sync.<cliente>.com/api/auth/login ...     # 200 token / 401 credenciales
curl https://sync.<cliente>.com/admin                          # 404 (catch-all)
curl https://sync.<cliente>.com/api/v2/admin/auditoria         # 404
curl https://sync.<cliente>.com/api/v1/routes/debug/doctos-pv  # 404
curl https://sync.<cliente>.com/api/v2/web/dashboard           # 404
curl "https://sync.<cliente>.com/api/v1/credito/clientes/abc/documentos"  # 404 (regex numérica)
curl "https://sync.<cliente>.com/api/v1/credito/clientes/12345/documentos" # pasa al origen
```

## 10. Monitoreo y fallas conocidas

| Síntoma | Causa probable | Acción |
|---|---|---|
| Error 1033 (Cloudflare) | Túnel caído (servicio detenido/sin salida a Internet) | `Restart-Service cloudflared`; revisar logs `%WinDir%\System32\config\systemprofile\.cloudflared\cloudflared.log` |
| 502 Bad Gateway | Túnel arriba pero Kestrel 5048 no responde | Verificar servicio RutxSincronizador y que arrancó en Production con `Network__ExternalApiEnabled=true` |
| Arranque abortado [SEGURIDAD] | Validador: AllowedHosts/PublicHostname/AdminPassword/JWT | Corregir variables de entorno del paso 7 |
| Latencia alta intermitente | Fallback LAN/Tailscale activo en build producción | Recompilar APK con `--dart-define=API_BASE_URL=https://...` (ver repo appmovil) |

## 11. Seguridad

- Nunca versionar `<TUNNEL_UUID>` real, JSON de credenciales ni `cert.pem`
  (excluidos en `.gitignore`).
- No publicar rutas nuevas sin actualizar simultáneamente: contrato móvil +
  allowlist ingress + pruebas.
- Rotación de credenciales del túnel: `cloudflared tunnel token --cred-file …`
  y reinicio del servicio.
