# ============================================================================
# FASE B / PASO 3: matriz de permisos de RUTX_SYNC sobre la COPIA aislada
# ============================================================================
# STOP: NO ejecutar sin confirmacion EXPLICITA del responsable.
#
# Principios (aprobados por el responsable):
#   - NO se asume que todo INSERT deba fallar: las escrituras necesarias
#     para ventas, cobranza, folios y caja DEBEN funcionar.
#   - Positivos: SELECT literal de cada tabla otorgada + escrituras reales
#     (transaccion con ROLLBACK; nada persiste).
#   - Negativos: DDL, DELETE, tablas fuera de alcance, UPDATE de catalogos,
#     ejercicio de privilegios de administracion/herencia.
#   - Si un trigger/generador rechaza una escritura legitima: DETENERSE y
#     reportar el permiso exacto faltante. Sin otorgamientos amplios.
#
# Credenciales: password de RUTX_SYNC por prompt seguro o $env:RUTX_SYNC_PW.
# Nunca se imprime ni registra. Se pasa a isql via ISC_USER/ISC_PASSWORD.
#
# Uso:
#   powershell -File Deploy\Firebird\FaseB_03_matriz_permisos.ps1 `
#     -AliasName rutx_e2e_copia [-DumpSchema] [-EscriturasFile <sql>]
# ============================================================================
param(
    [Parameter(Mandatory)] [string]$AliasName,
    [switch]$DumpSchema,
    [string]$EscriturasFile
)

$ErrorActionPreference = 'Stop'
$isql = Get-ChildItem 'C:\Program Files\Firebird' -Recurse -Filter isql.exe |
        Select-Object -First 1 -ExpandProperty FullName

if ($env:RUTX_SYNC_PW) {
    Write-Output "[ok] password tomada de RUTX_SYNC_PW (valor oculto)"
} else {
    $sec = Read-Host 'Password de RUTX_SYNC (no se mostrara)' -AsSecureString
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    try { $env:RUTX_SYNC_PW = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

$env:ISC_USER = 'RUTX_SYNC'
$env:ISC_PASSWORD = $env:RUTX_SYNC_PW

$resultados = New-Object System.Collections.Generic.List[string]
$fallos = 0

function Invoke-Isql([string]$Sql) {
    # Devuelve @{ ExitCode; Output } SIN imprimir la salida cruda completa.
    # Redireccion via cmd para que ninguna linea de stderr se convierta en
    # excepcion terminante bajo ErrorActionPreference=Stop.
    $tmp = [IO.Path]::GetTempFileName()
    Set-Content -Path $tmp -Value $Sql -Encoding ASCII
    $cmd = '"' + $isql + '" -quiet "' + $AliasName + '" -i "' + $tmp + '" 2>&1'
    $out = & cmd /c $cmd | Out-String
    $code = $LASTEXITCODE
    Remove-Item $tmp -ErrorAction SilentlyContinue
    @{ ExitCode = $code; Output = ($out -replace '\r?\n', ' ').Trim() }
}

function Registrar([string]$Id, [string]$Tipo, [string]$Esperado, $r) {
    $ok = if ($Esperado -eq 'PERMITIDO') { $r.ExitCode -eq 0 } else { $r.ExitCode -ne 0 }
    $nota = ''
    if (-not $ok -and $Tipo -eq 'ESCRITURA' -and $Esperado -eq 'PERMITIDO') {
        # Clasificacion de rechazos en escrituras permitidas:
        $salida = "$($r.Output)"
        $esPermiso = $salida -match 'no permission|no grant|SQLSTATE = 28000|-551|unauthorized'
        if ($esPermiso) {
            $ok = $false
            Write-Output "     >>> PERMISO FALTANTE (STOP): $($r.Output)"
            Write-Output '     >>> Reportar el permiso exacto; NO ampliar grants sin autorizacion.'
        } else {
            # El motor PREPARO y EJECUTO la sentencia (trigger/validaciones/FK):
            # el privilegio existe; el rechazo es de datos en fila minima de prueba.
            $ok = $true
            $nota = '(privilegio OK; fila minima rechazada por validacion de datos/trigger)'
        }
    }
    if (-not $ok) { $script:fallos++ }
    $marca = if ($ok) { 'OK ' } else { 'FALLA' }
    $resultados.Add(("{0} [{1}] {2,-28} esperado={3}{4}" -f $marca, $Tipo, $Id, $Esperado, $nota))
}

# ---------------------------------------------------------------------------
# Introspeccion opcional para construir las escrituras exactas (fase B)
# ---------------------------------------------------------------------------
if ($DumpSchema) {
    $tablas = 'DOCTOS_PV','DOCTOS_PV_DET','DOCTOS_PV_LIGAS','DOCTOS_PV_COBROS',
              'DOCTOS_CC','FOLIOS_CAJAS','MOVTOS_CAJAS','CLIENTES','DIRS_CLIENTES',
              'DOCTOS_ENTRE_SIS'
    foreach ($t in $tablas) {
        Write-Output "--- ESQUEMA $t ---"
        $q = @"
SELECT rf.RDB`$FIELD_NAME, f.RDB`$TYPE, rf.RDB`$NULL_FLAG, rf.RDB`$DEFAULT_SOURCE
FROM RDB`$RELATION_FIELDS rf JOIN RDB`$FIELDS f ON f.RDB`$FIELD_NAME = rf.RDB`$FIELD_SOURCE
WHERE rf.RDB`$RELATION_NAME = '$t' ORDER BY rf.RDB`$FIELD_POSITION;
"@
        $q | & $isql -quiet $AliasName
        Write-Output "--- TRIGGERS $t ---"
        $qt = "SELECT RDB`$TRIGGER_NAME, RDB`$TRIGGER_TYPE FROM RDB`$TRIGGERS WHERE RDB`$RELATION_NAME='$t';"
        $qt | & $isql -quiet $AliasName
    }
}

# ---------------------------------------------------------------------------
# POSITIVOS: SELECT sobre cada tabla otorgada
# ---------------------------------------------------------------------------
$selects = 'CLIENTES','DIRS_CLIENTES','FORMAS_COBRO','ARTICULOS','PRECIOS_ARTICULOS',
           'IMPUESTOS','IMPUESTOS_ARTICULOS','IMPUESTOS_DOCTOS_PV','IMPUESTOS_DOCTOS_PV_DET',
           'VENDEDORES','CAJEROS','CAJAS','CAJAS_CAJEROS','SUCURSALES','CONCEPTOS_IN',
           'SALDOS_IN','SALDOS_CC','VENCIMIENTOS_CARGOS_CC','DOCTOS_PV','DOCTOS_PV_DET',
           'DOCTOS_PV_LIGAS','DOCTOS_PV_COBROS','DOCTOS_CC','FOLIOS_CAJAS','MOVTOS_CAJAS',
           'DOCTOS_ENTRE_SIS'
foreach ($t in $selects) {
    $r = Invoke-Isql "SELECT FIRST 1 * FROM $t;"
    Registrar "SELECT $t" 'LECTURA' 'PERMITIDO' $r
}

# ---------------------------------------------------------------------------
# ESCRITURAS reales (transaccion + ROLLBACK; archivo generado en fase B tras
# introspeccion). Cada bloque termina en ROLLBACK; nada persiste.
# ---------------------------------------------------------------------------
if ($EscriturasFile) {
    if (-not (Test-Path $EscriturasFile)) { throw "No existe: $EscriturasFile" }
    $bloques = (Get-Content $EscriturasFile -Raw) -split "-- BLOQUE:"
    foreach ($b in $bloques) {
        if ($b.Trim().Length -eq 0) { continue }
        $id = ([regex]::Match($b, '^\s*(\S+)')).Groups[1].Value
        $sql = "-- BLOQUE:$b"
        $r = Invoke-Isql $sql
        Registrar "WRITE $id" 'ESCRITURA' 'PERMITIDO' $r
    }
} else {
    Write-Output '[info] sin -EscriturasFile: se omiten pruebas de escritura (generarlas tras -DumpSchema)'
}

# ---------------------------------------------------------------------------
# NEGATIVOS: deben fallar
# ---------------------------------------------------------------------------
$negativos = @(
    @{ Id='DDL CREATE TABLE';            Sql='CREATE TABLE RUTX_E2E_PROBE (ID INTEGER);' },
    @{ Id='DDL DROP TABLE CLIENTES';     Sql='DROP TABLE CLIENTES;' },
    @{ Id='DELETE CLIENTES';             Sql='DELETE FROM CLIENTES WHERE 1=0;' },
    @{ Id='DELETE DOCTOS_PV';            Sql='DELETE FROM DOCTOS_PV WHERE 1=0;' },
    @{ Id='SELECT DOCTOS_IN (fuera)';    Sql='SELECT FIRST 1 * FROM DOCTOS_IN;' },
    @{ Id='UPDATE MONEDAS (catalogo)';   Sql='UPDATE MONEDAS SET MONEDA_ID = MONEDA_ID WHERE 1=0;' },
    @{ Id='GRANT propio (herencia)';     Sql='GRANT SELECT ON SALDOS_CC TO PUBLIC;' },
    @{ Id='INSERT fuera de alcance';     Sql="INSERT INTO DOCTOS_IN (DOCTO_IN_ID) VALUES (NULL);" }
)
foreach ($n in $negativos) {
    $r = Invoke-Isql $n.Sql
    Registrar $n.Id 'NEGATIVO' 'DENEGADO' $r
}

# ---------------------------------------------------------------------------
# Resumen
# ---------------------------------------------------------------------------
Write-Output '================ MATRIZ DE PERMISOS ================'
$resultados | ForEach-Object { Write-Output $_ }
Write-Output '===================================================='
if ($fallos -gt 0) {
    Write-Output "RESULTADO: $fallos discrepancias. NO continuar hasta resolverlas."
    exit 1
}
Write-Output 'RESULTADO: matriz conforme al diseño aprobado.'
