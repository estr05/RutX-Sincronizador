using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit
{
    public class NoVentaSagaServiceTests
    {
        private readonly Mock<IWebSqliteStore> _storeMock;
        private readonly Mock<IFotoStorageService> _fotoMock;
        private readonly Mock<IVentaServicePv> _ventaMock;
        private readonly Mock<ILogger<NoVentaSagaService>> _loggerMock;
        private readonly NoVentaSagaService _service;

        public NoVentaSagaServiceTests()
        {
            _storeMock = new Mock<IWebSqliteStore>();
            _fotoMock = new Mock<IFotoStorageService>();
            _ventaMock = new Mock<IVentaServicePv>();
            _loggerMock = new Mock<ILogger<NoVentaSagaService>>();

            _service = new NoVentaSagaService(
                _storeMock.Object,
                _fotoMock.Object,
                _ventaMock.Object,
                _loggerMock.Object);
        }

        private NoSaleOperationRow CrearOperacion(string status, long? fotoFileId)
        {
            var sesion = new UsuarioSesion { Usuario = "TEST", VendedorId = 1, CajeroId = 1, CajaId = 1 };
            var form = new NoVentaPvCreateDto { VendedorId = 1 };

            return new NoSaleOperationRow(
                Id: 100,
                VentaMovilId: "MOVIL-123",
                RequestHash: "HASH",
                VendedorId: 1,
                ClienteId: 1,
                CausaId: 1,
                FechaHora: "2026-08-19",
                DoctoPvId: 500,
                Folio: "NOV-500",
                FotoFileId: fotoFileId,
                Status: status,
                Attempts: 1,
                ErrorCode: null,
                ErrorMessage: null,
                CreatedAt: "2026-08-19",
                UpdatedAt: "2026-08-19",
                PayloadJson: JsonSerializer.Serialize(form),
                SessionJson: JsonSerializer.Serialize(sesion));
        }

        [Fact]
        public async Task ProcesarReintentos_MediaNoEncontrada_CambiaARetryableFailed()
        {
            var op = CrearOperacion("media_promotion_pending", 999);
            _storeMock.Setup(s => s.ObtenerNoVentasParaReintentoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NoSaleOperationRow> { op });

            _storeMock.Setup(s => s.FindMediaFileByIdAsync(999, It.IsAny<CancellationToken>()))
                .ReturnsAsync((MediaFileRow?)null);

            await _service.ProcesarReintentosAsync(CancellationToken.None);

            _storeMock.Verify(s => s.UpdateNoSaleOperationAsync(100, "retryable_failed", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<long?>(), "MEDIA_NOT_FOUND", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(s => s.UpdateNoSaleOperationAsync(100, "completed", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ProcesarReintentos_PromoverLanzaExcepcion_CambiaARetryableFailed()
        {
            var op = CrearOperacion("media_promotion_pending", 999);
            var media = new MediaFileRow(999, "no_sale_photo", 100, "foto.jpg", "stored.jpg", "staging/stored.jpg", "image/jpeg", 1000, "hash", "staging", "", "");

            _storeMock.Setup(s => s.ObtenerNoVentasParaReintentoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NoSaleOperationRow> { op });

            _storeMock.Setup(s => s.FindMediaFileByIdAsync(999, It.IsAny<CancellationToken>()))
                .ReturnsAsync(media);

            _fotoMock.Setup(f => f.PromoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Error de red"));

            await _service.ProcesarReintentosAsync(CancellationToken.None);

            _storeMock.Verify(s => s.UpdateNoSaleOperationAsync(100, "retryable_failed", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<long?>(), "PROMO_FAILED", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(s => s.UpdateNoSaleOperationAsync(100, "completed", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ProcesarReintentos_MediaStaging_SePromueveYCompleta()
        {
            var op = CrearOperacion("media_promotion_pending", 999);
            var media = new MediaFileRow(999, "no_sale_photo", 100, "foto.jpg", "stored.jpg", "staging/stored.jpg", "image/jpeg", 1000, "hash", "staging", "", "");

            _storeMock.Setup(s => s.ObtenerNoVentasParaReintentoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NoSaleOperationRow> { op });

            _storeMock.Setup(s => s.FindMediaFileByIdAsync(999, It.IsAny<CancellationToken>()))
                .ReturnsAsync(media);

            _fotoMock.Setup(f => f.PromoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("Fotos/NoVentas/final.jpg");

            await _service.ProcesarReintentosAsync(CancellationToken.None);

            _storeMock.Verify(s => s.UpdateMediaFileStatusAsync(999, "completed", "Fotos/NoVentas/final.jpg", It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(s => s.UpdateNoSaleOperationAsync(100, "completed", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<long?>(), null, null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ProcesarReintentos_MediaYaCompletada_ReintentoIdempotente()
        {
            var op = CrearOperacion("media_promotion_pending", 999);
            var media = new MediaFileRow(999, "no_sale_photo", 100, "foto.jpg", "stored.jpg", "Fotos/NoVentas/final.jpg", "image/jpeg", 1000, "hash", "completed", "", "");

            _storeMock.Setup(s => s.ObtenerNoVentasParaReintentoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NoSaleOperationRow> { op });

            _storeMock.Setup(s => s.FindMediaFileByIdAsync(999, It.IsAny<CancellationToken>()))
                .ReturnsAsync(media);

            await _service.ProcesarReintentosAsync(CancellationToken.None);

            _fotoMock.Verify(f => f.PromoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _storeMock.Verify(s => s.UpdateNoSaleOperationAsync(100, "completed", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<long?>(), null, null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ProcesarReintentos_MediaEstadoInvalido_CambiaARetryableFailed()
        {
            var op = CrearOperacion("media_promotion_pending", 999);
            var media = new MediaFileRow(999, "no_sale_photo", 100, "foto.jpg", "stored.jpg", "staging/stored.jpg", "image/jpeg", 1000, "hash", "invalid_state", "", "");

            _storeMock.Setup(s => s.ObtenerNoVentasParaReintentoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NoSaleOperationRow> { op });

            _storeMock.Setup(s => s.FindMediaFileByIdAsync(999, It.IsAny<CancellationToken>()))
                .ReturnsAsync(media);

            await _service.ProcesarReintentosAsync(CancellationToken.None);

            _storeMock.Verify(s => s.UpdateNoSaleOperationAsync(100, "retryable_failed", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<long?>(), "MEDIA_BAD_STATE", It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
            _storeMock.Verify(s => s.UpdateNoSaleOperationAsync(100, "completed", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ProcesarReintentos_SinFoto_CompletaExitosamente()
        {
            var op = CrearOperacion("media_promotion_pending", null);

            _storeMock.Setup(s => s.ObtenerNoVentasParaReintentoAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<NoSaleOperationRow> { op });

            await _service.ProcesarReintentosAsync(CancellationToken.None);

            _fotoMock.Verify(f => f.PromoverAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _storeMock.Verify(s => s.UpdateNoSaleOperationAsync(100, "completed", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<long?>(), null, null, It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
