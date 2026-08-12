using System.Diagnostics;
using System.Net.Http;

namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Ventana principal del launcher: botones simples arriba
/// (Iniciar / Detener / Conf web / Servicio) + logs al centro.
/// Minimiza a bandeja al cerrar (no mata el proceso).
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
    private readonly NotifyIcon _trayIcon;
    private readonly Button _btnIniciar;
    private readonly Button _btnDetener;
    private readonly Button _btnConfWeb;
    private readonly Button _btnCopyLogs;
    private readonly Button _btnServicio;
    private readonly Label _lblEstado;
    private readonly RichTextBox _txtLogs;
    private readonly System.Windows.Forms.Timer _timerEstado = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _timerCopy = new() { Interval = 1500 };
    private string _rutaExe;
    private bool _servicioInstalado;
    private int _finBotones; // x donde terminan los botones de la barra superior

    public AdminForm()
    {
        // Localizar el exe del sync: instalacion estandarizada (instalacion.json),
        // ruta persistida, carpeta del launcher o bin del proyecto en desarrollo.
        var instalacion = InstalacionHelper.BuscarInstalacion();
        if (instalacion != null)
        {
            _rutaExe = instalacion.ExeSync;
            _sync.DefinirInstalacionRaiz(instalacion.Raiz);
        }
        else
        {
            _rutaExe = _sync.ResolverSyncExe() ?? "";
            PrimerArranqueSinInstalacion();
        }

        _servicioInstalado = ServiceHelper.Existe();

        Text = "RUTX · Sincronizador";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 520);
        Size = new Size(960, 620);
        BackColor = Fondo;
        Font = new Font("Figtree", 10F, FontStyle.Regular);

        // ===== Bandeja (NotifyIcon) =====
        _trayIcon = new NotifyIcon
        {
            Text = "RUTX Sincronizador",
            Visible = false,
            Icon = SystemIcons.Application
        };
        var menuTray = new ContextMenuStrip();
        menuTray.Items.Add("Abrir", null, (_, _) => MostrarVentana());
        menuTray.Items.Add(new ToolStripSeparator());
        menuTray.Items.Add("Iniciar servicio", null, (_, _) => EjecutarAccionServicio("iniciar"));
        menuTray.Items.Add("Detener servicio", null, (_, _) => EjecutarAccionServicio("detener"));
        menuTray.Items.Add(new ToolStripSeparator());
        menuTray.Items.Add("Salir", null, (_, _) => SalirDefinitivamente());
        _trayIcon.ContextMenuStrip = menuTray;
        _trayIcon.DoubleClick += (_, _) => MostrarVentana();

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

        _btnServicio = CrearBoton(
            _servicioInstalado ? "🔧 Servicio ON" : "🔧 Instalar servicio",
            _servicioInstalado ? Color.FromArgb(0x16, 0x6B, 0x34) : Color.FromArgb(0x6B, 0x72, 0x80),
            Color.White);
        _btnServicio.Size = new Size(170, 40); // "🔧 Instalar servicio" no cabia en 132px
        _btnServicio.Click += (_, _) => GestionarServicio();

        _lblEstado = new Label
        {
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Figtree", 10F, FontStyle.Bold),
            ForeColor = Rojo,
            Text = "●  Detenido"
        };

        topBar.Controls.Add(_btnIniciar);
        topBar.Controls.Add(_btnDetener);
        topBar.Controls.Add(_btnConfWeb);
        topBar.Controls.Add(_btnCopyLogs);
        topBar.Controls.Add(_btnServicio);
        topBar.Controls.Add(_lblEstado);

        // Posicionar botones a la izquierda
        int x = 14;
        foreach (var btn in new[] { _btnIniciar, _btnDetener, _btnConfWeb, _btnCopyLogs, _btnServicio })
        {
            btn.Location = new Point(x, 10);
            x += btn.Width + 8;
        }
        _finBotones = x;

        // El label de estado vive SIEMPRE a la derecha de los botones, ocupando
        // el espacio sobrante (se recorta con "…" si no alcanza). Antes quedaba
        // anclado al borde derecho y en ventanas estrechas se encimaba con los
        // botones 'Copy logs' y 'Servicio'.
        PosicionarEstado(topBar);
        topBar.Resize += (_, _) => PosicionarEstado(topBar);

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

        // Cerrar ventana = minimizar a bandeja (no matar proceso)
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                MinimizarABandeja();
                return;
            }
        };

        ActualizarEstado();
        AgregarLog("RUTX · Launcher del sincronizador");
        if (!string.IsNullOrEmpty(_rutaExe))
            AgregarLog("Sincronizador detectado: " + _rutaExe);
        else
            AgregarLog("ADVERTENCIA: no se localizo Rutx.Sincronizador.exe");
        if (_servicioInstalado)
            AgregarLog("Servicio Windows detectado: " + ServiceHelper.ObtenerEstado());
        AgregarLog("Presiona ▶ Iniciar para arrancar la API (puerto 5047).");
    }

    /// <summary>
    /// Primer arranque sin instalacion estandarizada (no existe instalacion.json):
    ///  - Si el sync esta JUNTO al launcher (despliegue en la PC del cliente,
    ///    misma carpeta de publicacion) se abre el asistente de instalacion
    ///    para crear la instalacion estandarizada (carpeta, BD, credenciales,
    ///    auditoria) copiando desde la propia carpeta del launcher.
    ///  - Si el sync no se localizo, tambien se abre el asistente (o el selector
    ///    manual de siempre si el usuario lo cancela).
    ///  - En desarrollo (sync en bin del repo, lejos del launcher) NO se molesta
    ///    con el asistente: la ventana queda lista con ▶ Iniciar.
    /// </summary>
    private void PrimerArranqueSinInstalacion()
    {
        bool syncJuntoAlLauncher = System.IO.File.Exists(
            Path.Combine(AppContext.BaseDirectory, "Rutx.Sincronizador.exe"));

        if (syncJuntoAlLauncher || string.IsNullOrEmpty(_rutaExe))
            InstalarOSeleccionar();
    }

    /// <summary>
    /// Ofrece el asistente de instalacion estandarizada; si el usuario lo
    /// cancela, cae al selector manual de siempre (OpenFileDialog). Devuelve el
    /// DialogResult del wizard o Cancel si se aborto todo.
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
            Size = new Size(124, 40),
            Font = new Font("Figtree", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
    }

    /// <summary>
    /// Ubica el label de estado en el espacio libre entre los botones y el borde
    /// derecho de la barra. Nunca se superpone a los botones: si el espacio no
    /// alcanza, AutoEllipsis recorta el texto con "…".
    /// </summary>
    private void PosicionarEstado(Panel barra)
    {
        int izquierda = _finBotones + 12;
        int ancho = barra.ClientSize.Width - izquierda - 16;
        _lblEstado.SetBounds(izquierda, 10, Math.Max(ancho, 1), 40);
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
        _lblEstado.Text = corriendo ? "●  En ejecución" : "●  Detenido";

        // Actualizar estado del servicio si esta instalado
        if (_servicioInstalado)
        {
            var svcEstado = ServiceHelper.ObtenerEstado();
            var corriendoSvc = svcEstado == System.ServiceProcess.ServiceControllerStatus.Running;
            _btnIniciar.Enabled = !corriendo && !corriendoSvc;
            _btnDetener.Enabled = corriendo || corriendoSvc;
            if (corriendoSvc)
            {
                _lblEstado.ForeColor = VerdeInfo;
                _lblEstado.Text = "●  Servicio activo";
            }
        }
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

    // ==================================================================
    // BANDEJA Y SERVICIO
    // ==================================================================

    private void MinimizarABandeja()
    {
        Hide();
        _trayIcon.Visible = true;
        _trayIcon.ShowBalloonTip(2000, "RUTX Sincronizador", "El sincronizador sigue corriendo en segundo plano.", ToolTipIcon.Info);
    }

    private void MostrarVentana()
    {
        Show();
        WindowState = FormWindowState.Normal;
        _trayIcon.Visible = false;
        Activate();
    }

    private void SalirDefinitivamente()
    {
        _timerEstado.Stop();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _sync.Dispose();
        Application.Exit();
    }

    private async void EjecutarAccionServicio(string accion)
    {
        if (!ServiceHelper.Existe())
        {
            MostrarVentana();
            AgregarLog("El servicio no esta instalado. Usa el boton 'Instalar servicio'.");
            return;
        }

        // Iniciar/detener un servicio de Windows requiere permisos de
        // administrador; si no los tenemos, relanzar elevado (UAC).
        if (!UacHelper.EsAdministrador())
        {
            var arg = accion == "iniciar" ? "--iniciar-servicio" : "--detener-servicio";
            AgregarLog($"Solicitando permisos de administrador para {accion} el servicio...");
            using var proc = UacHelper.RelanzarComoAdministrador(arg);
            if (proc == null)
            {
                AgregarLog($"ERROR: se cancelo la solicitud de permisos de administrador. No se {accion switch { "iniciar" => "inicio", _ => "detuvo" }} el servicio.");
                return;
            }
            await proc.WaitForExitAsync();
            ActualizarEstado();
            return;
        }

        var (ok, msg) = accion switch
        {
            "iniciar" => await Task.Run(() => ServiceHelper.Iniciar()),
            "detener" => await Task.Run(() => ServiceHelper.Detener()),
            _ => (false, "Accion desconocida")
        };
        AgregarLog(ok ? msg : $"ERROR: {msg}");
        ActualizarEstado();
    }

    private async void GestionarServicio()
    {
        if (_servicioInstalado)
        {
            // Mostrar opciones del servicio
            var resultado = MessageBox.Show(
                "El servicio esta instalado.\n\n" +
                "SI = Iniciar servicio\n" +
                "NO = Detener servicio\n" +
                "CANCEL = No hacer nada",
                "RUTX · Gestionar servicio",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (resultado == DialogResult.Yes)
                EjecutarAccionServicio("iniciar");
            else if (resultado == DialogResult.No)
                EjecutarAccionServicio("detener");
        }
        else
        {
            // Instalar servicio
            if (string.IsNullOrEmpty(_rutaExe) || !System.IO.File.Exists(_rutaExe))
            {
                AgregarLog("ERROR: no se encontro Rutx.Sincronizador.exe. No se puede instalar el servicio.");
                return;
            }

            var confirm = MessageBox.Show(
                "Esto instalara el Sincronizador como servicio de Windows.\n\n" +
                "- Arrancara automaticamente con Windows\n" +
                "- No morira al cerrar sesion\n" +
                "- Se reiniciara si falla\n\n" +
                "Requiere permisos de administrador.\n\n" +
                "¿Continuar?",
                "RUTX · Instalar servicio",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            AgregarLog("Instalando servicio Windows...");
            _btnServicio.Enabled = false; // evitar doble clic mientras se instala
            try
            {
                // Instalar un servicio requiere administrador. Sin elevacion,
                // sc.exe falla con "OpenSCManager ERROR 5: Acceso denegado"; por
                // eso, si no somos administradores, relanzamos la app elevada con
                // el prompt de UAC y ahi se realiza la instalacion.
                if (!UacHelper.EsAdministrador())
                {
                    AgregarLog("Solicitando permisos de administrador...");
                    using var proc = UacHelper.RelanzarComoAdministrador(
                        $"--instalar-servicio \"{_rutaExe}\"");
                    if (proc == null)
                    {
                        AgregarLog("ERROR: no se pudieron obtener permisos de administrador. No se instalo el servicio.");
                        MessageBox.Show(
                            "Para instalar el servicio se necesitan permisos de administrador.\n\n" +
                            "Ejecuta el launcher como administrador o vuelve a intentarlo aceptando el aviso \"¿Quieres permitir que esta aplicación haga cambios en este dispositivo?\".",
                            "RUTX · Instalar servicio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Esperar a que el proceso elevado termine (ahi se muestra el
                    // resultado) y refrescar el estado.
                    await proc.WaitForExitAsync();
                    _servicioInstalado = ServiceHelper.Existe();
                    if (_servicioInstalado)
                    {
                        _btnServicio.Text = "🔧 Servicio ON";
                        _btnServicio.BackColor = Color.FromArgb(0x16, 0x6B, 0x34);
                        AgregarLog("Servicio instalado correctamente.");
                    }
                    else
                    {
                        AgregarLog("ERROR: el proceso elevado no logro instalar el servicio (revisa el mensaje que se mostro).");
                    }
                    ActualizarEstado();
                    return;
                }

                // Ya somos administrador: instalar en este proceso
                var (ok, msg) = await Task.Run(() => ServiceHelper.Instalar(_rutaExe));
                AgregarLog(ok ? msg : $"ERROR: {msg}");
                if (ok)
                {
                    _servicioInstalado = true;
                    _btnServicio.Text = "🔧 Servicio ON";
                    _btnServicio.BackColor = Color.FromArgb(0x16, 0x6B, 0x34);
                }
                else
                {
                    MessageBox.Show(
                        $"No se pudo instalar el servicio.\n\n{msg}",
                        "RUTX · Instalar servicio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                ActualizarEstado();
            }
            finally
            {
                _btnServicio.Enabled = true;
            }
        }
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
