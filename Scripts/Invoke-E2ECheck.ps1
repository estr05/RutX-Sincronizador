# ============================================================================
# FASE C: verificador E2E server-side (LAN / Tailscale / loopback / tunel)
# ============================================================================
# Lee credenciales SOLO de appsettings.Local.json (no versionado). NUNCA
# imprime passwords, JWT ni cadenas de conexion: solo codigos HTTP,
# tiempos y veredictos por endpoint.
#
# Uso:
#   powershell -File Scripts\Invoke-E2ECheck.ps1 -BaseUrl http://100.71.116.89:5047 `
#     -Operaciones health,login,sync
#
# Operaciones:
#   health  GET  /health
#   login   POST /api/auth/login          (credenciales de WebAuth en Local.json)
#   sync    GET  /api/v1/routes/sync      (requiere login previo en la misma corrida)
# ============================================================================
param(
    [Parameter(Mandatory)] [string]$BaseUrl,
    [ValidateSet('health','login','sync')]
    [string[]]$Operaciones = @('health','login','sync'),
    [string]$LocalJsonPath = (Join-Path $PSScriptRoot '..\appsettings.Local.json')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $LocalJsonPath)) {
    throw "No existe appsettings.Local.json en '$LocalJsonPath'. Crealo desde appsettings.Local.example.json."
}

# --- Lectura silenciosa de credenciales (nunca se muestran) ------------------
$cfg = Get-Content $LocalJsonPath -Raw | ConvertFrom-Json
$usuario = $cfg.WebAuth.AdminUsername
$password = $cfg.WebAuth.AdminPassword
if ([string]::IsNullOrWhiteSpace($usuario) -or [string]::IsNullOrWhiteSpace($password)) {
    throw "WebAuth.AdminUsername/AdminPassword vacios en Local.json; complétalos tú mismo."
}
Write-Output ("[ok] credenciales cargadas (usuario len={0}, password len={1})" -f $usuario.Length, $password.Length)

$BaseUrl = $BaseUrl.TrimEnd('/')
$token = $null

function Invoke-Check([string]$Nombre, [scriptblock]$Accion) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $codigo = & $Accion
        $sw.Stop()
        $veredicto = if ($codigo -ge 200 -and $codigo -lt 300) { 'OK' } else { 'FALLO' }
        Write-Output ("[{0}] {1} -> HTTP {2} ({3} ms)" -f $veredicto, $Nombre, $codigo, $sw.ElapsedMilliseconds)
        return ($veredicto -eq 'OK')
    } catch {
        $sw.Stop()
        Write-Output ("[FALLO] {0} -> excepcion de red ({1} ms): {2}" -f $Nombre, $sw.ElapsedMilliseconds, $_.Exception.Message)
        return $false
    }
}

$resultadoGlobal = $true

foreach ($op in $Operaciones) {
    switch ($op) {

        'health' {
            $ok = Invoke-Check 'GET /health' {
                $r = Invoke-WebRequest -Uri "$BaseUrl/health" -Method Get -UseBasicParsing -TimeoutSec 10
                $r.StatusCode
            }
            $resultadoGlobal = $resultadoGlobal -and $ok
        }

        'login' {
            if ($token) { Write-Output '[skip] login ya realizado en esta corrida'; continue }
            $body = @{ username = $usuario; password = $password } | ConvertTo-Json
            $tokCapturado = $null
            $ok = Invoke-Check 'POST /api/auth/login' {
                $r = Invoke-WebRequest -Uri "$BaseUrl/api/auth/login" -Method Post `
                     -ContentType 'application/json' -Body $body -UseBasicParsing -TimeoutSec 15
                # Captura el JWT solo en memoria; jamas se imprime.
                $json = $r.Content | ConvertFrom-Json
                $script:tokCapturado = if ($json.token) { $json.token }
                                       elseif ($json.accessToken) { $json.accessToken }
                                       elseif ($json.data.token) { $json.data.token }
                $r.StatusCode
            }
            if ($ok) {
                $token = $tokCapturado
                Write-Output ("       JWT recibido (len={0}, valor oculto)" -f ($token | ForEach-Object { $_.Length }))
            } else {
                $resultadoGlobal = $false
            }
        }

        'sync' {
            if (-not $token) { Write-Output '[FALLO] sync requiere login previo (-Operaciones health,login,sync)'; $resultadoGlobal = $false; continue }
            $ok = Invoke-Check 'GET /api/v1/routes/sync' {
                $r = Invoke-WebRequest -Uri "$BaseUrl/api/v1/routes/sync" -Method Get `
                     -Headers @{ Authorization = "Bearer $token" } -UseBasicParsing -TimeoutSec 60
                $r.StatusCode
            }
            $resultadoGlobal = $resultadoGlobal -and $ok
        }
    }
}

if ($resultadoGlobal) { Write-Output 'RESULTADO: todas las operaciones OK.' ; exit 0 }
Write-Output 'RESULTADO: hubo fallos. Detener y reportar causa (no relajar seguridad).'
exit 1
