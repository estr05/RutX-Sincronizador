using Rutx.Sincronizador.Services;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

public class SqlErrorClassifierTest
{
    [Fact]
    public void ViolacionLlaveForanea_SeClasificaConConstraintYTabla()
    {
        var ex = new Exception(
            "Statement failed, SQLSTATE = 23000\n" +
            "violation of FOREIGN KEY constraint \"ARTS_A_DOCTOS_PV_DET\" on table \"DOCTOS_PV_DET\"");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.ViolacionLlaveForanea, info.Tipo);
        Assert.Equal("ARTS_A_DOCTOS_PV_DET", info.Constraint);
        Assert.Equal("DOCTOS_PV_DET", info.Tabla);
        Assert.False(info.EsReintentable);
        Assert.Contains("llave foranea", info.MensajeAmigable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViolacionNotNull_ExtraeLaColumna()
    {
        var ex = new Exception("cannot insert NULL into (ES_FAC_GLOBAL)");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.ViolacionNotNull, info.Tipo);
        Assert.Equal("ES_FAC_GLOBAL", info.Columna);
        Assert.False(info.EsReintentable);
    }

    [Fact]
    public void ViolacionNotNull_PorValidationError_SeClasificaComoCheck()
    {
        // SQLCODE -286 es una violacion de dominio/CHECK (valor invalido), NO
        // un NULL en columna NOT NULL (que da "cannot insert NULL into").
        // Antes se clasificaba como ViolacionNotNull (falso positivo); ahora
        // se clasifica como ViolacionCheck.
        var ex = new Exception(
            "SQL error code = -286\nvalidation error for column NOMBRE, value *** null");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.ViolacionCheck, info.Tipo);
        Assert.Equal(-286, info.SqlCode);
    }

    [Fact]
    public void ViolacionCheck_SeClasificaConConstraint()
    {
        var ex = new Exception(
            "violation of CHECK constraint \"DOCTOS_PV_ESTATUS\" on table \"DOCTOS_PV\"");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.ViolacionCheck, info.Tipo);
        Assert.Equal("DOCTOS_PV_ESTATUS", info.Constraint);
    }

    [Fact]
    public void ValorDuplicado_SeClasifica()
    {
        var ex = new Exception(
            "attempt to store duplicate value (visible to active transactions) in unique index \"FOLIOS_CAJAS_UK\"");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.ValorDuplicado, info.Tipo);
    }

    [Fact]
    public void TablaDesconocida_SeClasifica()
    {
        var ex = new Exception("Dynamic SQL Error\nSQL error code = -204\nTable unknown \"VENDEDORESX\"");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.TablaOColumnaInexistente, info.Tipo);
        Assert.Equal("VENDEDORESX", info.Tabla);
    }

    [Fact]
    public void ColumnaDesconocida_SeClasifica()
    {
        var ex = new Exception("SQL error code = -204\nColumn unknown \"POSICIONX\"");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.TablaOColumnaInexistente, info.Tipo);
        Assert.Equal("POSICIONX", info.Columna);
    }

    [Fact]
    public void ErrorDeConexion_EsReintentable()
    {
        var ex = new Exception(
            "SocketException: No connection could be made because the target machine actively refused it. 127.0.0.1:3050");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.ErrorConexion, info.Tipo);
        Assert.True(info.EsReintentable);
    }

    [Fact]
    public void OdsNoSoportada_SeClasifica()
    {
        var ex = new Exception(
            "unsupported on-disk structure for file C:\\MICROSIP DATOS CRUZROJASCLC.FDB; found 13.1, support 12.2");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.OdsNoSoportada, info.Tipo);
        Assert.False(info.EsReintentable);
        Assert.Contains("Firebird", info.MensajeAmigable);
    }

    [Fact]
    public void Deadlock_EsReintentable()
    {
        var ex = new Exception("deadlock\nupdate conflicts with concurrent update");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.BloqueoODeadlock, info.Tipo);
        Assert.True(info.EsReintentable);
    }

    [Fact]
    public void ErrorAnidado_RecorreInnerExceptions()
    {
        var fb = new Exception(
            "violation of FOREIGN KEY constraint \"CAJEROS_A_DOCTOS_PV\" on table \"DOCTOS_PV\"");
        var ex = new Exception("Error interno al guardar la venta.", fb);

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.ViolacionLlaveForanea, info.Tipo);
        Assert.Equal("CAJEROS_A_DOCTOS_PV", info.Constraint);
    }

    [Fact]
    public void MensajeNoSql_QuedaComoDesconocido()
    {
        var ex = new Exception("No se encontro la caja configurada.");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.Desconocido, info.Tipo);
    }

    [Fact]
    public void SintaxisSql_SeClasifica()
    {
        var ex = new Exception("Dynamic SQL Error\nSQL error code = -104\nToken unknown - line 1, column 8\nFOO");

        var info = SqlErrorClassifier.Clasificar(ex);

        Assert.Equal(SqlErrorTipo.ErrorSintaxisSql, info.Tipo);
        Assert.Equal(-104, info.SqlCode);
    }
}
