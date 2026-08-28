<#
.SYNOPSIS
    Gate anti-secretos del Sincronizador RutX.

.DESCRIPTION
    Detecta VALORES OPERATIVOS en archivos versionados (no palabras sueltas):
      1. La clave JWT vieja filtrada (literal exacto).
      2. Contraseñas operativas en JSON (valores distintos de CHANGE_ME*).
      3. Jwt:Key operativa en JSON (>=32 chars sin placeholder).
      4. Password/PWD dentro de cadenas de conexión en JSON (sin placeholder).
      5. La palabra 'masterkey' fuera de la ALLOWLIST deliberada
         (el validador que la rechaza y los fixtures de pruebas la usan
         legítimamente para detectarla; el propio scanner la define).

    Exit code 1 si hay algún hallazgo (rompe pre-commit y CI).

.PARAMETER IncludeHistory
    Escanea además todo el historial (git log --all -p). Usado por
    Purga-Historial.md para verificar que los literales rotados ya no existen.

.EXAMPLE
    pwsh Scripts/Invoke-SecretScan.ps1
    pwsh Scripts/Invoke-SecretScan.ps1 -IncludeHistory
#>
[CmdletBinding()]
param(
    [switch]$IncludeHistory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# ALLOWLIST deliberada (prefijos de ruta relativos al repo).
# Cada entrada justifica por qué el patrón crudo puede aparecer ahí SIN ser
# un secreto operativo. Mantener mínima y comentada.
# ---------------------------------------------------------------------------
$allowlist = @(
    # Rechaza 'masterkey' como contraseña por defecto (defensa, no uso operativo)
    'Security/ProductionConfigurationValidator.cs',
    # Fixtures xunit deliberados (desglose de cadenas Firebird con el default)
    'Rutx.Sincronizador.Tests/',
    # Definiciones de patrones de ESTE script
    'Scripts/Invoke-SecretScan.ps1',
    # El hook invoca al scanner y menciona los patrones
    'githooks/',
    # Menciones documentales del password por defecto y del procedimiento de purga
    'Docs/CONFIGURACION_PRODUCCION.md',
    'Deploy/Cloudflare/README.md',
    'Purga-Historial.md',
    # Auditoría Microsip: menciones documentales del default ('default: masterkey')
    'Integracion/PROMPT_AUDITORIA.md',
    'Integracion/auditar_compatibilidad.py'
)

function Test-Allowlisted([string]$relativePath)
{
    foreach ($prefix in $allowlist)
    {
        if ($relativePath -eq $prefix.TrimEnd('/')) { return $true }
        if ($relativePath.StartsWith($prefix.Replace('\', '/'), [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

$jwtVieja = 'RUTX_SECRET_KEY_PRODUCTION_SECURE_KEY_GENERATED_32_BYTES_MINIMUM_2026_TOKEN'

$patterns = @(
    @{ Name = 'JWT vieja filtrada (literal)';        Regex = [regex]::Escape($jwtVieja); JsonOnly = $false },
    @{ Name = 'Contraseña operativa en JSON';        Regex = '"(Admin)?[Pp]assword"\s*:\s*"(?!CHANGE_ME)[^"]{3,}"'; JsonOnly = $true },
    @{ Name = 'Jwt:Key operativa en JSON';           Regex = '"Key"\s*:\s*"(?!CHANGE_ME)[A-Za-z0-9+/=]{32,}"'; JsonOnly = $true },
    @{ Name = 'Password en cadena de conexión JSON'; Regex = '(?i)(?<![\w])(?:password|pwd)=(?!CHANGE_ME)[^;"\r\n]{3,}'; JsonOnly = $true },
    @{ Name = "'masterkey' fuera de allowlist";      Regex = '(?i)masterkey'; JsonOnly = $false }
)

$hits = New-Object System.Collections.Generic.List[string]

function Add-Hit([string]$message) { $script:hits.Add($message) }

# --- 1. Árbol de trabajo (archivos versionados) ---------------------------
$tracked = & git ls-files
foreach ($file in $tracked)
{
    if ([string]::IsNullOrWhiteSpace($file)) { continue }
    $rel = $file.Replace('\', '/')
    $isJson = $rel -match '\.json$'
    $allowed = Test-Allowlisted $rel

    $bytes = [IO.File]::ReadAllBytes($file)
    if ($null -eq $bytes -or $bytes.Length -eq 0) { continue }

    # Heurística binaria simple: byte NUL en los primeros 8 KB
    $probeLen = [Math]::Min(8192, $bytes.Length)
    $binary = $false
    for ($i = 0; $i -lt $probeLen; $i++) { if ($bytes[$i] -eq 0) { $binary = $true; break } }
    if ($binary) { continue }

    $text = [Text.Encoding]::UTF8.GetString($bytes)
    $lines = $text -split "`n"

    for ($ln = 0; $ln -lt $lines.Length; $ln++)
    {
        foreach ($p in $patterns)
        {
            # Patrones JSON-only (valores operativos en configuracion):
            # aplican SIEMPRE, incluso en archivos allowlistados.
            if ($p.JsonOnly -and -not $isJson) { continue }

            # Patrones crudos (palabra 'masterkey', JWT vieja literal):
            # se omiten SOLO en archivos allowlistados deliberadamente.
            if (-not $p.JsonOnly -and $allowed) { continue }

            if ($p.Regex -is [regex])
            {
                if (-not $p.Regex.IsMatch($lines[$ln])) { continue }
            }
            elseif ($lines[$ln] -notmatch $p.Regex) { continue }

            Add-Hit ("{0}:{1}: [{2}] {3}" -f $rel, ($ln + 1), $p.Name, $lines[$ln].Trim())
            break
        }
    }
}

# --- 2. Historial completo (opcional) --------------------------------------
if ($IncludeHistory)
{
    Write-Output '[..] Escaneando historial completo (git log --all -p)...'
    $patchText = & git log --all -p --no-color | Out-String
    foreach ($p in $patterns)
    {
        if ($p.Regex -is [regex])
        {
            $m = $p.Regex.Matches($patchText)
        }
        else
        {
            $m = [regex]::Matches($patchText, $p.Regex, [Text.RegularExpressions.RegexOptions]::Multiline)
        }
        if ($m.Count -gt 0)
        {
            Add-Hit ("HISTORIAL: [{0}] {1} coincidencia(s)" -f $p.Name, $m.Count)
        }
    }
}

# --- Resultado --------------------------------------------------------------
if ($hits.Count -gt 0)
{
    Write-Output ''
    Write-Output 'FALLO: posibles secretos operativos detectados:'
    $hits | ForEach-Object { Write-Output "  $_" }
    Write-Output ''
    Write-Output 'Corrige el valor (usa placeholders CHANGE_ME_*), o justifica y agrega a $allowlist'
    Write-Output 'con comentario SI ES un fixture/documento deliberado.'
    exit 1
}

if ($IncludeHistory) { Write-Output 'OK: sin secretos operativos en archivos versionados ni en el historial.' }
else { Write-Output 'OK: sin secretos operativos en archivos versionados.' }
exit 0
