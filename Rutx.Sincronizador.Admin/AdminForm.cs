using System.Diagnostics;
using System.Net.Http;

namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Ventana principal del launcher: botones simples arriba
/// (Iniciar / Detener / Conf web) + logs del sincronizador al centro.
/// Estilos: azul marino #003859, naranja #F6AD55, rojo #EF4444, grises neutros.
/// Logs: verde INFO, amarillo WARN, rojo ERROR (consola Windows).
/// </summary>
public class AdminForm : Form
{
    // Paleta (estandar del panel RUTX)
    private static readonly Color AzulMarino = Color.FromArgb(0x00, 0x38, 0x59);
    private static readonly Color AzulMarinoHover = Color.FromArgb(0x00, 0x4C, 0x73);
    private static readonly Color Naranja = Color.FromArgb(0xF6, 0xAD, 0x55);
    private static readonly Color Rojo = Color.FromArgb(0xEF, 0x44, 0x44);
    private static readonly Color RojoHover = Color.FromArgb(0xDC, 0x26, 0x26);
    private static readonly Color Fondo = Color.FromArgb(0xF9, 0xFA, 0xFB);
    private static readonly Color Borde = Color.FromArgb(0xE5, 0xE7, 0xEB);
    private static readonly Color ConsolaFondo = Color.FromArgb(0x0B, 0x11, 0x20);
    private static readonly Color ConsolaTexto = Color.FromArgb(0xE2, 0xE8, 0xF0);
    private static readonly Color VerdeInfo = Color.FromArgb(0x4A, 0xDE, 0x80);
    private static readonly Color AmarilloWarn = Color.FromArgb(0xFB, 0xBF, 0x24);
    private static readonly Color RojoError = Color.FromArgb(0xF8, 0x71, 0x71);

    private readonly SyncProcessController _sync = new();
    private readonly Button _btnIniciar;
    private readonly Button _btnDetener;
    private readonly Button _btnConfWeb;
    private readonly Button _btnCopyLogs;
    private readonly Label _lblEstado;
    private readonly RichTextBox _txtLogs;
    private readonly System.Windows.Forms.Timer _timerEstado = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _timerCopy = new() { Interval = 1500 };
    private string _rutaExe;

    public AdminForm()
    {
        // Localizar el exe del sync (instalacion estandarizada, ruta persistida,
        // carpeta del launcher o bin del proyecto en desarrollo)
        _rutaExe = _sync.ResolverSyncExe() ?? "";

        // PRIMER ARRANQUE sin sync localizado: abrir el asistente de instalacion
        // (elige carpeta, BD y credenciales, instala y audita). Si el usuario lo
        // cancela, se cae al selector manual de siempre y la ventana sigue
        // mostrandose con el aviso de sync no localizado.
        if (string.IsNullOrEmpty(_rutaExe))
        {
            InstalarOSeleccionar();
        }

        Text = "RUTX · Sincronizador";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 520);
        Size = new Size(960, 620);
        BackColor = Fondo;
        Font = new Font("Figtree", 10F, FontStyle.Regular);

        // ===== Barra superior con botones =====
        var topBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 64,
            BackColor = Color.White,
            Padding = new Padding(14, 10, 14, 10)
        };
        topBar.Paint += (_, e) =>
        {
            using var pen = new Pen(Borde, 1);
            e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
        };

        _btnIniciar = CrearBoton("▶  Iniciar", AzulMarino, Color.White);
        _btnIniciar.Click += (_, _) => IniciarSync();

        _btnDetener = CrearBoton("⏹  Detener", Rojo, Color.White);
        _btnDetener.Click += (_, _) => _sync.Detener();
        _btnDetener.Enabled = false;

        _btnConfWeb = CrearBoton("⚙  Conf (web)", Naranja, AzulMarino);
        _btnConfWeb.Click += (_, _) => AbrirPanelWeb();

        _btnCopyLogs = CrearBoton("📋  Copy logs", Color.White, AzulMarino);
        _btnCopyLogs.FlatAppearance.BorderSize = 1;
        _btnCopyLogs.FlatAppearance.BorderColor = Borde;
        _btnCopyLogs.Click += (_, _) => CopiarLogs();
        _timerCopy.Tick += (_, _) =>
        {
            _timerCopy.Stop();
            _btnCopyLogs.Text = "📋  Copy logs";
        };

        _lblEstado = new Label
        {
            AutoSize = false,
            Size = new Size(240, 40),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Figtree", 10F, FontStyle.Bold),
            ForeColor = Rojo,
            Text = "●  Detenido",
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        topBar.Controls.Add(_btnIniciar);
        topBar.Controls.Add(_btnDetener);
        topBar.Controls.Add(_btnConfWeb);
        topBar.Controls.Add(_btnCopyLogs);
        topBar.Controls.Add(_lblEstado);

        // Posicionar botones a la izquierda
        int x = 16;
        foreach (var btn in new[] { _btnIniciar, _btnDetener, _btnConfWeb, _btnCopyLogs })
        {
            btn.Location = new Point(x, 10);
            x += btn.Width + 10;
        }
        _lblEstado.Location = new Point(topBar.ClientSize.Width - _lblEstado.Width - 16, 10);
        topBar.Resize += (_, _) =>
            _lblEstado.Location = new Point(topBar.ClientSize.Width - _lblEstado.Width - 16, 10);

        // ===== Logs al centro (consola) =====
        _txtLogs = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = ConsolaFondo,
            ForeColor = ConsolaTexto,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Cascadia Code", 10F),
            DetectUrls = false
        };

        var panelLogs = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = ConsolaFondo,
            Padding = new Padding(14)
        };
        panelLogs.Controls.Add(_txtLogs);

        Controls.Add(panelLogs);
        Controls.Add(topBar);

        // ===== Eventos del proceso =====
        _sync.LogLine += (_, linea) => AgregarLog(linea);
        _sync.EstadoCambio += (_, _) => ActualizarEstado();
        _timerEstado.Tick += (_, _) => ActualizarEstado();
        _timerEstado.Start();
        FormClosed += (_, _) => { _timerEstado.Stop(); _sync.Dispose(); };

        ActualizarEstado();
        AgregarLog("RUTX · Launcher del sincronizador");
        if (!string.IsNullOrEmpty(_rutaExe))
            AgregarLog("Sincronizador detectado: " + _rutaExe);
        else
            AgregarLog("ADVERTENCIA: no se localizo Rutx.Sincronizador.exe");
        AgregarLog("Presiona ▶ Iniciar para arrancar la API (puerto 5047).");
    }

    /// <summary>
    /// Primer arranque sin sincronizador localizado: ofrece el asistente de
    /// instalacion estandarizada; si el usuario lo cancela, cae al selector
    /// manual de siempre (OpenFileDialog). Devuelve el DialogResult del wizard
    /// o Cancel si se aborto todo.
    /// </summary>
    private DialogResult InstalarOSeleccionar()
    {
        // Carpeta fuente del build del sync: en produccion se publica junto al
        // launcher (misma carpeta). En desarrollo ResolverSyncExe ya la ubico;
        // si no, el wizard permite localizar Rutx.Sincronizador.exe a mano.
        var carpetaFuente = !string.IsNullOrEmpty(_rutaExe)
            ? Path.GetDirectoryName(_rutaExe) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;

        using var wizard = new InstalacionWizardForm(carpetaFuente, onInstalada: (exeSync, raiz) =>
        {
            _rutaExe = exeSync;
            _sync.GuardarRuta(exeSync);
            _sync.DefinirInstalacionRaiz(raiz);
        });

        var resultado = wizard.ShowDialog(this);
        if (resultado == DialogResult.OK && !string.IsNullOrEmpty(_rutaExe))
            return resultado;

        // El usuario no instalo: flujo anterior (selector manual)
        using var dialogo = new OpenFileDialog
        {
            Filter = "Sincronizador|Rutx.Sincronizador.exe|Todos|*.exe",
            Title = "Ubica Rutx.Sincronizador.exe"
        };
        if (dialogo.ShowDialog(this) == DialogResult.OK)
        {
            _rutaExe = dialogo.FileName;
            _sync.GuardarRuta(_rutaExe);
            return DialogResult.OK;
        }
        return DialogResult.Cancel;
    }

    private static Button CrearBoton(string texto, Color fondo, Color frente)
    {
        return new Button
        {
            Text = texto,
            BackColor = fondo,
            ForeColor = frente,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            Size = new Size(132, 40),
            Font = new Font("Figtree", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
    }

    private void IniciarSync()
    {
        if (string.IsNullOrEmpty(_rutaExe) || !System.IO.File.Exists(_rutaExe))
        {
            AgregarLog("ERROR: no se encontro Rutx.Sincronizador.exe");
            return;
        }
        _sync.Iniciar(_rutaExe);
    }

    private async void AbrirPanelWeb()
    {
        _btnConfWeb.Enabled = false;
        try
        {
            // 1) Si el sync no esta corriendo, iniciarlo primero
            if (!_sync.IsRunning)
            {
                AgregarLog("El sincronizador no esta corriendo: iniciandolo...");
                IniciarSync();
            }

            // 2) Esperar a que /health responda (max ~15 s)
            AgregarLog("Esperando respuesta de la API en :5047...");
            bool listo = false;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (int i = 0; i < 30; i++)
            {
                if (!_sync.IsRunning) break; // se detuvo mientras esperaba
                try
                {
                    var resp = await http.GetAsync("http://localhost:5047/health");
                    if (resp.IsSuccessStatusCode)
                    {
                        listo = true;
                        break;
                    }
                }
                catch { /* aun no levanta: reintentar */ }
                await Task.Delay(500);
            }

            // 3) Abrir el navegador solo si la API respondio
            if (!listo)
            {
                AgregarLog("ERROR: la API en :5047 no respondio. Revisa los logs del proceso (puede fallar la conexion a la BD).");
                MessageBox.Show(
                    "El sincronizador no respondio en localhost:5047.\n\nRevisa los logs de esta ventana: si falla la conexion a la BD, configurala primero en el panel o appsettings.json.",
                    "RUTX · Sincronizador", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            AgregarLog("API lista. Abriendo panel web...");
            Process.Start(new ProcessStartInfo("http://localhost:5047/admin")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AgregarLog("ERROR al abrir el navegador: " + ex.Message);
        }
        finally
        {
            _btnConfWeb.Enabled = true;
        }
    }

    private void CopiarLogs()
    {
        if (_txtLogs.IsDisposed) return;
        if (_txtLogs.TextLength == 0)
        {
            AgregarLog("No hay logs que copiar aun.");
            return;
        }
        try
        {
            Clipboard.SetText(_txtLogs.Text);
            _btnCopyLogs.Text = "✓  Copiado";
            _timerCopy.Start();
        }
        catch (Exception ex)
        {
            AgregarLog("ERROR al copiar logs: " + ex.Message);
        }
    }

    private void ActualizarEstado()
    {
        if (_btnIniciar.IsDisposed) return;
        if (_btnIniciar.InvokeRequired)
        {
            _btnIniciar.BeginInvoke(() => ActualizarEstado());
            return;
        }

        bool corriendo = _sync.IsRunning;
        _btnIniciar.Enabled = !corriendo;
        _btnDetener.Enabled = corriendo;
        _lblEstado.ForeColor = corriendo ? VerdeInfo : Rojo;
        _lblEstado.Text = corriendo ? "●  En ejecución · :5047" : "●  Detenido";
    }

    private void AgregarLog(string linea)
    {
        if (_txtLogs.IsDisposed) return;
        if (_txtLogs.InvokeRequired)
        {
            _txtLogs.BeginInvoke(() => AgregarLog(linea));
            return;
        }

        var nivel = NivelDe(linea);
        var color = nivel switch
        {
            "WRN" => AmarilloWarn,
            "ERR" => RojoError,
            _ => VerdeInfo
        };

        _txtLogs.SelectionStart = _txtLogs.TextLength;
        _txtLogs.SelectionColor = color;
        _txtLogs.SelectionFont = new Font(_txtLogs.Font.FontFamily, _txtLogs.Font.Size, nivel == "INF" ? FontStyle.Regular : FontStyle.Bold);
        _txtLogs.AppendText($"[{DateTime.Now:HH:mm:ss}] [{nivel}] {linea}\n");
        _txtLogs.SelectionColor = ConsolaTexto;
        _txtLogs.ScrollToCaret();

        if (_txtLogs.TextLength > 400_000)
            _txtLogs.Clear();
    }

    private static string NivelDe(string linea)
    {
        var l = linea.ToLowerInvariant();
        if (l.Contains("error") || l.Contains("fail") || l.Contains("fatal") || l.Contains("exception"))
            return "ERR";
        if (l.Contains("warn") || l.Contains("advertencia"))
            return "WRN";
        return "INF";
    }
}
