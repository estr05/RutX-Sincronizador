using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Rutx.Sincronizador.Models.Web;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Tests.Web;

public class ReportsWebServiceTests
{
    private Mock<IConfiguration> _configMock;
    private Mock<ILogger<ReportsWebService>> _loggerMock;
    private Mock<IDashboardWebService> _dashboardMock;

    public ReportsWebServiceTests()
    {
        _configMock = new Mock<IConfiguration>();
        _loggerMock = new Mock<ILogger<ReportsWebService>>();
        _dashboardMock = new Mock<IDashboardWebService>();
        
        var sectionMock = new Mock<IConfigurationSection>();
        sectionMock.Setup(s => s.Value).Returns("true");
        _configMock.Setup(c => c.GetSection("WebFeatures:Reports")).Returns(sectionMock.Object);
    }
    
    [Fact]
    public void Constructor_LanzaExcepcion_SiNoHayConexion()
    {
        Assert.Throws<InvalidOperationException>(() => new ReportsWebService(_configMock.Object, _loggerMock.Object, _dashboardMock.Object));
    }
}
