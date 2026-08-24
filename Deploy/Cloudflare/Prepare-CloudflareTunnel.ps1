<#
.SYNOPSIS
    Materializa la configuracion real del tunel de Cloudflare para el
    Sincronizador RutX a partir de config.yml.example.

.DESCRIPTION
    - Valida el UUID del tunel y el JSON de credenciales.
    - Genera config.yml con <TUNNEL_UUID> y <HOSTNAME> sustituidos.
    - Copia el config.yml y el JSON de credenciales al perfil del
      servicio Windows (systemprofile\.cloudflared), siguiendo el modelo
      oficial de cloudflared como servicio en Windows.
    - Aplica ACL: FullControl para Administradores/SYSTEM y solo lectura
      para la cuenta REAL del servicio cloudflared (resuelta desde el
      propio servicio instalado; nunca asume NETWORK SERVICE).
    - Si cloudflared esta en PATH, ejecuta 'tunnel ingress validate'.

    NO crea DNS, NO instala servicios y NO contiene valores reales en el repo.

.EXAMPLE
    .\Prepare-CloudflareTunnel.ps1 -TunnelId 6ff42ae2-7659-4d4f-9db4-3a3c5f1c2b9a -Hostname sync.cliente.com -CredentialsJson C:\ruta\6ff42ae2-....json

.EXAMPLE
    .\Prepare-CloudflareTunnel.ps1 ... -ServiceAccount "NT AUTHORITY\SYSTEM"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$TunnelId,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9.-]+\.[A-Za-z]{2,}$')]
    [string]$Hostname,

    [Parameter(Mandatory = $true)]
    [string]$CredentialsJson,

    [string]$ConfigOut = "C:\Windows\System32\config\systemprofile\.cloudflared\config.yml",

    # Opcional: fuerza la cuenta del servicio. Si se omite, se resuelve desde Win32_Service.
    [string]$ServiceAccount
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- 1. Validar GUID del tunel -------------------------------------
$guidParsed = [Guid]::Empty
if (-not [Guid]::TryParse($TunnelId, [ref]$guidParsed))
{
    throw "[-] TunnelId '$TunnelId' no es un GUID valido (obtenlo de 'cloudflared tunnel create <nombre>')."
}
$tunnelIdNorm = "$guidParsed"

# --- 2. Validar JSON de credenciales -------------------------------
if (-not (Test-Path -LiteralPath $CredentialsJson))
{
    throw "[-] No existe el archivo de credenciales: $CredentialsJson"
}
$expectedJsonName = "$tunnelIdNorm.json"
$jsonName = [IO.Path]::GetFileName($CredentialsJson)
if ($jsonName -ne $expectedJsonName)
{
    throw "[-] El JSON de credenciales debe llamarse '$expectedJsonName' (es '$jsonName')."
}

# --- 3. Materializar config.yml ------------------------------------
$examplePath = Join-Path $PSScriptRoot 'config.yml.example'
if (-not (Test-Path -LiteralPath $examplePath))
{
    throw "[-] No se encontro la plantilla: $examplePath"
}
$configContent = (Get-Content -LiteralPath $examplePath -Raw)
$configContent = $configContent.Replace('<TUNNEL_UUID>', $tunnelIdNorm).Replace('<HOSTNAME>', $Hostname)

$configDir = Split-Path -Parent $ConfigOut
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
Set-Content -LiteralPath $ConfigOut -Value $configContent -Encoding UTF8
Write-Output "[OK] config.yml materializado: $ConfigOut"

# --- 4. Copiar credenciales ----------------------------------------
$credDest = Join-Path $configDir $expectedJsonName
Copy-Item -LiteralPath $CredentialsJson -Destination $credDest -Force
Write-Output "[OK] Credenciales copiadas: $credDest"

# --- 5. Resolver la cuenta REAL del servicio cloudflared ------------
if (-not $ServiceAccount)
{
    $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='cloudflared'" -ErrorAction SilentlyContinue
    if ($svc -and $svc.StartName)
    {
        $ServiceAccount = $svc.StartName
        Write-Output "[OK] Cuenta resuelta del servicio 'cloudflared': $ServiceAccount"
    }
    else
    {
        throw @'
[-] No se encontro el servicio Windows 'cloudflared' (o no expone StartName).
    Instala primero el servicio ('cloudflared.exe service install') o pasa
    explicitamente -ServiceAccount "<dominio>\<cuenta>".
'@
    }
}

# --- 6. ACLs --------------------------------------------------------
icacls $configDir /inheritance:r /grant 'Administrators:(OI)(CI)F' /grant 'SYSTEM:(OI)(CI)F' | Out-Null
icacls $ConfigOut /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null
icacls $credDest   /inheritance:r /grant 'Administrators:F' /grant 'SYSTEM:F' | Out-Null
icacls $ConfigOut /grant "${ServiceAccount}:R" | Out-Null
icacls $credDest   /grant "${ServiceAccount}:R" | Out-Null
Write-Output "[OK] ACL aplicadas (Admins/SYSTEM full; '$ServiceAccount' solo lectura)"

# --- 7. Validacion de ingress (si cloudflared esta disponible) ------
$cfCmd = Get-Command cloudflared -ErrorAction SilentlyContinue
if ($cfCmd)
{
    Write-Output "[..] Ejecutando validacion de ingress..."
    & cloudflared --config="$ConfigOut" tunnel ingress validate
    if ($LASTEXITCODE -eq 0) { Write-Output "[OK] Ingress valido." }
    else { Write-Warning "[!] 'ingress validate' devolvio codigo $LASTEXITCODE; revisa el config.yml." }
}
else
{
    Write-Warning "[AVISO] cloudflared no esta en PATH; omite 'tunnel ingress validate'. Ejecutalo manualmente:"
    Write-Output "       cloudflared --config=$ConfigOut tunnel ingress validate"
}

# --- 8. Proximos pasos ----------------------------------------------
Write-Output @'

SIGUIENTES PASOS MANUALES (no automatizados a proposito):
  1) DNS (requiere dominio DELEGADO a Cloudflare):
       cloudflared tunnel route dns rutx-mobile-production $Hostname
     (o CNAME manual: $Hostname -> <TUNNEL_UUID>.cfargotunnel.com)
  2) Instalar/reniciar el servicio:
       cloudflared.exe service install   (si aun no existe)
       Restart-Service cloudflared
  3) Verificar desde red externa:
       https://$Hostname/health                 -> 200
       POST https://$Hostname/api/auth/login    -> 200/401
       https://$Hostname/admin                  -> 404 (catch-all)
'@ -replace '\$Hostname', $Hostname
