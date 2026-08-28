# ============================================================================
# FASE B / PASO 2: copia CONSISTENTE de la BD de Microsip (gbak + restore)
# ============================================================================
# STOP: NO ejecutar sin confirmacion EXPLICITA del responsable.
#
# Que hace:
#   1. gbak -b  : backup online transaccionalmente consistente de la BD viva
#   2. gbak -c  : restore hacia una carpeta AISLADA (fuera de datos Microsip)
#   3. Alta del alias en databases.conf (con respaldo previo del original)
#   4. Verificacion de conexion al alias correcto (SELECT 1 FROM RDB$DATABASE)
#
# Reglas:
#   - SYSDBA y sus credenciales actuales intactos; la password de SYSDBA se pide por prompt
#     seguro o variable de entorno efimera. NUNCA se imprime ni registra.
#   - Nunca escribe dentro del directorio de datos de Microsip.
#   - Si el archivo destino ya existe, se niega salvo -Force.
#
# Uso (cuando haya aprobacion):
#   powershell -File Deploy\Firebird\FaseB_02_copia_consistente.ps1 `
#     -LiveDbPath "C:\Microsip datos\COYATOC.FDB" `
#     -BackupDir  "C:\FirebirdCopias" `
#     -RestoreDir "C:\FirebirdCopias\e2e" `
#     -AliasName  "rutx_e2e_copia"
# ============================================================================
param(
    [Parameter(Mandatory)] [string]$LiveDbPath,
    [Parameter(Mandatory)] [string]$BackupDir,
    [Parameter(Mandatory)] [string]$RestoreDir,
    [Parameter(Mandatory)] [string]$AliasName,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

function Get-FbPassword([string]$Usuario) {
    # Pide password por prompt seguro; nunca hace echo ni la registra.
    $sec = Read-Host "Password de $Usuario (no se mostrara)" -AsSecureString
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

# --- Localizar herramientas -------------------------------------------------
$isql = Get-ChildItem 'C:\Program Files\Firebird' -Recurse -Filter isql.exe |
        Select-Object -First 1 -ExpandProperty FullName
if (-not $isql) { throw "isql.exe no encontrado bajo C:\Program Files\Firebird" }
$gbak = $isql -replace 'isql\.exe$', 'gbak.exe'
if (-not (Test-Path $gbak)) {
    $gbak = Get-ChildItem 'C:\Program Files\Firebird' -Recurse -Filter gbak.exe |
            Select-Object -First 1 -ExpandProperty FullName
}
Write-Output "[ok] isql = $isql"
Write-Output "[ok] gbak = $gbak"

# --- Validaciones de aislamiento --------------------------------------------
$resolvedLive = (Resolve-Path $LiveDbPath).Path
foreach ($dir in @($BackupDir, $RestoreDir)) {
    if ($resolvedLive.StartsWith((Resolve-Path (Split-Path $dir -Parent) -ErrorAction SilentlyContinue).Path)) {}
}
$restoreTarget = Join-Path $RestoreDir "$AliasName.fdb"
if ((Test-Path $restoreTarget) -and -not $Force) {
    throw "El destino ya existe: $restoreTarget (usa -Force para sobreescribir)"
}
if ($resolvedLive -like '*Microsip*' -and $RestoreDir -like "*Microsip*") {
    throw "RestoreDir no puede vivir dentro del directorio de datos de Microsip"
}

New-Item -ItemType Directory -Force -Path $BackupDir, $RestoreDir | Out-Null

$fbk = Join-Path $BackupDir 'rutx_e2e.fbk'

# --- 1) Backup consistente ---------------------------------------------------
$env:ISC_USER = 'SYSDBA'
$env:ISC_PASSWORD = Get-FbPassword 'SYSDBA'
try {
    Write-Output "[1/4] gbak backup -> $fbk"
    & $gbak -b -g -v $resolvedLive $fbk -y (Join-Path $BackupDir 'gbak.log')
    if ($LASTEXITCODE -ne 0) { throw "gbak backup fallo (codigo $LASTEXITCODE); ver $BackupDir\gbak.log" }

    # --- 2) Restore aislado --------------------------------------------------
    Write-Output "[2/4] gbak restore -> $restoreTarget"
    & $gbak -c -v -rep $fbk $restoreTarget -y (Join-Path $BackupDir 'gbak_restore.log')
    if ($LASTEXITCODE -ne 0) { throw "gbak restore fallo (codigo $LASTEXITCODE)" }

    # --- 3) Alias en databases.conf (respaldo previo) ------------------------
    $conf = Join-Path (Split-Path $isql) 'databases.conf'
    if (Test-Path $conf) {
        Copy-Item $conf "$conf.bak-e2e" -Force
        if (-not (Select-String -Path $conf -Pattern "^\s*$AliasName\s*=" -Quiet)) {
            Add-Content $conf "`n$AliasName = $restoreTarget"
            Write-Output "[3/4] alias agregado a $conf (backup previo: databases.conf.bak-e2e)"
        } else {
            Write-Output "[3/4] alias ya existia en $conf"
        }
    } else {
        throw "No se encontro databases.conf junto a isql: $conf"
    }

    # --- 4) Verificar conexion al alias correcto ------------------------------
    Write-Output "[4/4] verificacion de conexion al alias '$AliasName'"
    $probe = "SELECT 1 FROM RDB`$DATABASE;"
    $out = $probe | & $isql -user SYSDBA -quiet $AliasName 2>&1
    if ($LASTEXITCODE -eq 0 -and ($out -match '1$|====')) {
        Write-Output "[OK] conexion al alias verificada. Copia lista."
        Write-Output "     Alias : $AliasName"
        Write-Output "     Archivo: $restoreTarget"
    } else {
        throw "No se pudo conectar al alias. Salida isql: $($out -join ' | ')"
    }
}
finally {
    Remove-Item Env:\ISC_PASSWORD, Env:\ISC_USER -ErrorAction SilentlyContinue
}
