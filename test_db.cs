using System;
using System.Linq;
using FirebirdSql.Data.FirebirdClient;
using Dapper;

class Program {
    static void Main() {
        var cs = $"DataSource=localhost;Database=C:\\Microsip datos\\RUTX.FDB;User=SYSDBA;Password={Environment.GetEnvironmentVariable("RUTX_TESTDB_PASSWORD")}";
        using var conn = new FbConnection(cs);
        var count = conn.QueryFirstOrDefault<int>("SELECT COUNT(*) FROM DOCTOS_PV WHERE TIPO_DOCTO='V' AND ESTATUS='N'");
        Console.WriteLine("Total Ventas validas en BD: " + count);
        
        var minDate = conn.QueryFirstOrDefault<DateTime?>("SELECT MIN(FECHA) FROM DOCTOS_PV WHERE TIPO_DOCTO='V' AND ESTATUS='N'");
        var maxDate = conn.QueryFirstOrDefault<DateTime?>("SELECT MAX(FECHA) FROM DOCTOS_PV WHERE TIPO_DOCTO='V' AND ESTATUS='N'");
        Console.WriteLine("Primera venta: " + minDate);
        Console.WriteLine("Ultima venta: " + maxDate);
    }
}
