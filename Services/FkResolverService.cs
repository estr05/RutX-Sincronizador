// ============================================================================
// ARCHIVO: FkResolverService.cs
// PROPOSITO: Resuelve valores validos para Foreign Keys consultando
//            los metadatos de Firebird de forma dinamica.
//
// FLUJO:
//   1. Consulta RDB$RELATION_CONSTRAINTS para descubrir la tabla y columna
//      referenciada por la FK constraint
//   2. Busca un registro que coincida con el sourceValue/sourceColumn
//   3. Si no encuentra, busca el primer registro disponible en la tabla padre
//   4. Fallback a defaultValue
//
// USO TIPICO:
//   var cajeroId = await resolver.ResolveFkAsync(
//       conn, tx, "DOCTOS_PV", "CAJERO_ID", "CAJEROS_A_DOCTOS_PV",
//       "VENDEDOR_ID", ventaDto.VendedorId,
//       config.GetValue<int>("MicrosipSettings:DefaultCajeroId", 2419));
// ============================================================================

using System;
using System.Threading.Tasks;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Resuelve valores validos para Foreign Keys consultando metadatos de Firebird.
/// </summary>
public class FkResolverService : IFkResolverService
{
    private readonly ILogger<FkResolverService> _logger;

    public FkResolverService(ILogger<FkResolverService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resuelve un valor valido para una FK.
    /// </summary>
    /// <param name="connection">Conexion Firebird abierta</param>
    /// <param name="transaction">Transaccion activa (puede ser null)</param>
    /// <param name="childTable">Tabla que contiene la FK (ej: DOCTOS_PV)</param>
    /// <param name="fkConstraintName">Nombre de la FK constraint (ej: CAJEROS_A_DOCTOS_PV)</param>
    /// <param name="sourceColumn">Columna usada para buscar relacion (ej: VENDEDOR_ID)</param>
    /// <param name="sourceValue">Valor del sourceColumn (ej: 9449)</param>
    /// <param name="defaultValue">Valor por defecto si no se encuentra nada</param>
    /// <returns>ID valido para usar en la FK</returns>
    public async Task<int> ResolveFkAsync(
        FbConnection connection,
        FbTransaction? transaction,
        string fkConstraintName,
        string? sourceColumn,
        int? sourceValue,
        int defaultValue)
    {
        try
        {
            // PASO 1: Descubrir la tabla y columna padre referenciada por la FK
            var sqlFkInfo = @"
                SELECT
                    trim(rc_ref.RDB$RELATION_NAME) AS PARENT_TABLE,
                    trim(ise2.RDB$FIELD_NAME) AS PARENT_COLUMN
                FROM RDB$RELATION_CONSTRAINTS rc
                JOIN RDB$REF_CONSTRAINTS refc ON refc.RDB$CONSTRAINT_NAME = rc.RDB$CONSTRAINT_NAME
                JOIN RDB$RELATION_CONSTRAINTS rc_ref ON trim(rc_ref.RDB$CONSTRAINT_NAME) = trim(refc.RDB$CONST_NAME_UQ)
                JOIN RDB$INDEX_SEGMENTS ise2 ON trim(ise2.RDB$INDEX_NAME) = trim(rc_ref.RDB$INDEX_NAME)
                WHERE rc.RDB$CONSTRAINT_NAME = @FkName
                  AND rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY'";

            var fkInfo = await connection.QueryFirstOrDefaultAsync(
                sqlFkInfo,
                new { FkName = fkConstraintName },
                transaction: transaction);

            if (fkInfo == null)
            {
                _logger.LogWarning("[FK] No se encontro la constraint {FkName} en los metadatos. Usando default={Default}", fkConstraintName, defaultValue);
                return defaultValue;
            }

            string parentTable = (fkInfo.PARENT_TABLE as string ?? "").Trim();
            string parentColumn = (fkInfo.PARENT_COLUMN as string ?? "").Trim();

            _logger.LogInformation("[FK] Descubierto: {FkName} -> {ParentTable}.{ParentColumn}", fkConstraintName, parentTable, parentColumn);

            if (string.IsNullOrEmpty(parentTable))
            {
                _logger.LogWarning("[FK] Tabla padre vacia para {FkName}. Default={Default}", fkConstraintName, defaultValue);
                return defaultValue;
            }

            // PASO 2: Si tenemos sourceColumn/sourceValue, buscar por esa relacion
            if (!string.IsNullOrEmpty(sourceColumn) && sourceValue.HasValue && EsNombreSeguro(sourceColumn) && EsNombreSeguro(parentColumn) && EsNombreSeguro(parentTable))
            {
                var sqlFind = $"SELECT FIRST 1 {parentColumn} FROM {parentTable} WHERE {sourceColumn} = @Value";
                var foundId = await connection.QueryFirstOrDefaultAsync<int?>(
                    sqlFind,
                    new { Value = sourceValue.Value },
                    transaction: transaction);

                if (foundId.HasValue)
                {
                    _logger.LogInformation("[FK] Resuelto: {ParentTable}.{SourceColumn}={SourceValue} -> {ParentColumn}={FoundId}", parentTable, sourceColumn, sourceValue, parentColumn, foundId.Value);
                    return foundId.Value;
                }

                _logger.LogWarning("[FK] No se encontro {ParentTable}.{SourceColumn}={SourceValue}. Buscando primer registro disponible...", parentTable, sourceColumn, sourceValue);
            }

            // PASO 3: Buscar el primer registro disponible en la tabla padre
            if (!EsNombreSeguro(parentColumn) || !EsNombreSeguro(parentTable))
                return defaultValue;
            var sqlFirst = $"SELECT FIRST 1 {parentColumn} FROM {parentTable} ORDER BY {parentColumn}";
            var firstId = await connection.QueryFirstOrDefaultAsync<int?>(
                sqlFirst,
                null,
                transaction: transaction);

            if (firstId.HasValue)
            {
                _logger.LogInformation("[FK] Primer ID disponible en {ParentTable}.{ParentColumn} = {FirstId}", parentTable, parentColumn, firstId.Value);
                return firstId.Value;
            }

            _logger.LogWarning("[FK] No hay registros en {ParentTable}. Usando default={Default}", parentTable, defaultValue);
            return defaultValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FK] Error al resolver {FkName}. Usando default={Default}", fkConstraintName, defaultValue);
            return defaultValue;
        }
    }

    private static bool EsNombreSeguro(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Length > 128) return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}

public interface IFkResolverService
{
    Task<int> ResolveFkAsync(
        FbConnection connection,
        FbTransaction? transaction,
        string fkConstraintName,
        string? sourceColumn,
        int? sourceValue,
        int defaultValue);
}
