using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FirebirdSql.Data.FirebirdClient;
using Dapper;

namespace Rutx.Sincronizador.Controllers.Admin;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DiagnosticController : ControllerBase
{
    private readonly string _connectionString;

    public DiagnosticController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException("FirebirdConnection not found.");
    }

    [HttpGet("impuestos-detalle")]
    public async Task<IActionResult> ExplorarImpuestosDetalle()
    {
        using var conn = new FbConnection(_connectionString);

        // 1. Estructura de IMPUESTOS_DOCTOS_PV_DET
        var columnasDet = await conn.QueryAsync(@"
            SELECT rf.RDB$FIELD_NAME AS COLUMN_NAME,
                   f.RDB$FIELD_TYPE AS FIELD_TYPE,
                   f.RDB$FIELD_LENGTH AS FIELD_LENGTH,
                   f.RDB$FIELD_SCALE AS FIELD_SCALE,
                   rf.RDB$NULL_FLAG AS NULL_FLAG
            FROM RDB$RELATION_FIELDS rf
            JOIN RDB$FIELDS f ON f.RDB$FIELD_NAME = rf.RDB$FIELD_SOURCE
            WHERE rf.RDB$RELATION_NAME = 'IMPUESTOS_DOCTOS_PV_DET'
            ORDER BY rf.RDB$FIELD_POSITION");

        // 2. Triggers relacionados con DOCTOS_PV
        var triggers = await conn.QueryAsync(@"
            SELECT RDB$TRIGGER_NAME AS TRIGGER_NAME,
                   RDB$RELATION_NAME AS TABLE_NAME,
                   RDB$TRIGGER_TYPE AS TRIGGER_TYPE,
                   RDB$TRIGGER_SEQUENCE AS TRIGGER_SEQUENCE,
                   RDB$TRIGGER_INACTIVE AS INACTIVE
            FROM RDB$TRIGGERS
            WHERE RDB$RELATION_NAME IN ('DOCTOS_PV', 'DOCTOS_PV_DET', 'IMPUESTOS_DOCTOS_PV',
                                         'IMPUESTOS_DOCTOS_PV_DET', 'DOCTOS_PV_COBROS')
              AND RDB$SYSTEM_FLAG = 0
            ORDER BY RDB$RELATION_NAME, RDB$TRIGGER_TYPE");

        // 3. Muestra de datos de IMPUESTOS_DOCTOS_PV_DET (si existe)
        var tieneDatos = false;
        var sampleData = new List<Dictionary<string, object?>>();
        try
        {
            var raw = await conn.QueryAsync(@"SELECT FIRST 5 * FROM IMPUESTOS_DOCTOS_PV_DET");
            sampleData = raw.Select(row =>
            {
                var dict = new Dictionary<string, object?>();
                foreach (var prop in (IDictionary<string, object?>)row)
                    dict[prop.Key] = prop.Value;
                return dict;
            }).ToList();
            tieneDatos = sampleData.Any();
        }
        catch { }

        // 4. Muestra de DOCTOS_PV existentes con PROCESO_ORIGEN
        var muestraDoctos = await conn.QueryAsync(@"
            SELECT FIRST 5 DOCTO_PV_ID, FOLIO, CAJA_ID, VENDEDOR_ID, PROCESO_ORIGEN,
                   IMPORTE_NETO, TOTAL_IMPUESTOS, SISTEMA_ORIGEN
            FROM DOCTOS_PV
            WHERE SISTEMA_ORIGEN = 'PV'
            ORDER BY DOCTO_PV_ID DESC");

        // 5. Verificar si IMPUESTOS_DOCTOS_PV_DET requiere FK a DOCTOS_PV_DET
        var fkDet = await conn.QueryAsync(@"
            SELECT rc.RDB$CONSTRAINT_NAME AS CONSTRAINT_NAME,
                   rc.RDB$CONSTRAINT_TYPE AS CONSTRAINT_TYPE,
                   isg.RDB$FIELD_NAME AS FIELD_NAME
            FROM RDB$RELATION_CONSTRAINTS rc
            JOIN RDB$INDEX_SEGMENTS isg ON isg.RDB$INDEX_NAME = rc.RDB$INDEX_NAME
            WHERE rc.RDB$RELATION_NAME = 'IMPUESTOS_DOCTOS_PV_DET'
              AND rc.RDB$CONSTRAINT_TYPE = 'FOREIGN KEY'");

        return Ok(new
        {
            columnas_impuestos_doctos_pv_det = columnasDet.Select(c => new
            {
                nombre = ((string)c.COLUMN_NAME).Trim(),
                tipo = c.FIELD_TYPE,
                longitud = c.FIELD_LENGTH,
                escala = c.FIELD_SCALE,
                not_null = c.NULL_FLAG != null
            }).ToList(),
            triggers = triggers.Select(t => new
            {
                nombre = ((string)t.TRIGGER_NAME).Trim(),
                tabla = ((string)t.TABLE_NAME).Trim(),
                tipo = t.TRIGGER_TYPE,
                secuencia = t.TRIGGER_SEQUENCE,
                inactivo = t.INACTIVE
            }).ToList(),
            tiene_datos_impuestos_pv_det = tieneDatos,
            muestra_datos_pv_det = sampleData,
            muestra_doctos_pv = muestraDoctos.Select(row =>
            {
                var dict = new Dictionary<string, object?>();
                foreach (var prop in (IDictionary<string, object?>)row)
                    dict[prop.Key] = prop.Value;
                return dict;
            }).ToList(),
            foreign_keys_pv_det = fkDet.Select(f => new
            {
                constraint_name = ((string)f.CONSTRAINT_NAME).Trim(),
                constraint_type = ((string)f.CONSTRAINT_TYPE).Trim(),
                field_name = ((string)f.FIELD_NAME).Trim()
            }).ToList()
        });
    }

    [HttpGet("columns/{tableName}")]
    public async Task<IActionResult> GetColumns(string tableName)
    {
        using var conn = new FbConnection(_connectionString);
        var cols = await conn.QueryAsync(@"
            SELECT TRIM(rf.RDB$FIELD_NAME) AS column_name
            FROM RDB$RELATION_FIELDS rf
            WHERE rf.RDB$RELATION_NAME = @TableName
            ORDER BY rf.RDB$FIELD_POSITION",
            new { TableName = tableName.ToUpperInvariant() });
        return Ok(new { table = tableName, columns = cols.Select(c => (string)c.COLUMN_NAME) });
    }
}
