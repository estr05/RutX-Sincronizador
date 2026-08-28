using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Text.Json;

namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Asistente de instalacion estandarizada del sincronizador (4 pasos):
///   1. Ubicacion   -> carpeta destino (default C:\ProgramData\RUTX\Sincronizador)
///   2. BD + creds  -> elegir el .fdb, verificar firma y probar conexion
///                     (si la conexión inicial falla, pide credenciales válidas)
///   3. Instalar    -> copia ejecutables, genera appsettings.json y marcador,
///                     arranca el sync con la raiz como WorkingDirectory y
///                     ejecuta la auditoria EN VIVO (contadores 🔴🟡🟢)
///   4. Finalizar   -> resumen y cierre
/// </summary>
public class InstalacionWizardForm : Form
{
    // ===================== Paleta (estandar RUTX) =====================
    private static readonly Color AzulMarino = Color.FromArgb(0x00, 0x38, 0x59);
    private static readonly Color AzulMarinoHover = Color.FromArgb(0x00, 0x4C, 0x73);
    private static readonly Color Naranja = Color.FromArgb(0xF6, 0xAD, 0x55);
    private static readonly Color Rojo = Color.FromArgb(0xEF, 0x44, 0x44);
    private static readonly Color Fondo = Color.FromArgb(0xF9, 0xFA, 0xFB);
    private static readonly Color Borde = Color.FromArgb(0xE5, 0xE7, 0xEB);
    private static readonly Color TextoMuted = Color.FromArgb(0x6B, 0x72, 0x80);
    private static readonly Color ConsolaFondo = Color.FromArgb(0x0B, 0x11, 0x20);
    private static readonly Color ConsolaTexto = Color.FromArgb(0xE2, 0xE8, 0xF0);
    private static readonly Color VerdeInfo = Color.FromArgb(0x4A, 0xDE, 0x80);
    private static readonly Color AmarilloWarn = Color.FromArgb(0xFB, 0xBF, 0x24);
    private static readonly Color RojoError = Color.FromArgb(0xF8, 0x71, 0x71);

    // ===================== Estado del wizard =====================
    private readonly string _carpetaFuenteSync;   // folder con el build del sync
    private readonly Action<string, string>? _onInstalada; // (exeSync, raiz)

    private string _raiz = InstalacionHelper.RutaDefault;
    private string _rutaFdb = "";
    private string _usuario = "SYSDBA";
    private string _password = "";
    private bool _conexionOk;
    private FirebirdVersionInfo? _versionInfo;
    private bool _instalado;
    private bool _auditoriaOk;
    private int _nOk, _nAvisos, _nFallos;

    private int _paso = 1;
    private readonly Label[] _pasosIndicador = new Label[4];
    private readonly Panel[] _paneles = new Panel[4];
    private readonly Button _btnAtras;
    private readonly Button _btnSiguiente;
    private readonly Button _btnFinalizar;

    // Paso 1 (se asignan en CrearPanel* llamado desde el constructor)
    private TextBox _txtRaiz = null!;
    private Label _lblEstructura = null!;
    // Paso 2
    private TextBox _txtFdb = null!;
    private TextBox _txtUsuario = null!;
    private TextBox _txtPassword = null!;
    private TextBox _txtWebPassword = null!;
    private string _webPassword = "";
    private Label _lblConexion = null!;
    private Button _btnProbar = null!;
    // Paso 3
    private CheckBox _chkAccesoMovil = null!;
    private TextBox _txtHostname = null!;
    private Label _lblHostname = null!;
    private RichTextBox _txtProgreso = null!;
    private Button _btnInstalar = null!;
    private Label _lblContadores = null!;
    private Label _lblEstadoInstalacion = null!;
    private Label _lblSugerencia = null!;

    // Indicadores de carga
    private readonly SpinnerCircular _spinnerConexion = new();
    private readonly SpinnerCircular _spinnerInstalar = new();
    // Paso 4
    private Label _lblResumen = null!;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(150) };

    public InstalacionWizardForm(string carpetaFuenteSync, InstalacionInfo? degradada = null, Action<string, string>? onInstalada = null)
    {
        _carpetaFuenteSync = carpetaFuenteSync;
        _onInstalada = onInstalada;
        if (degradada != null)
        {
            _raiz = degradada.Raiz;
            _rutaFdb = degradada.BdPath;
            _usuario = degradada.UsuarioFb;
        }

        Text = "RUTX · Instalación del Sincronizador";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(780, 620);
        Size = new Size(860, 620);
        BackColor = Fondo;
        Font = new Font("Figtree", 10F, FontStyle.Regular);

        // ===================== Encabezado =====================
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 86,
            BackColor = AzulMarino,
            Padding = new Padding(22, 16, 22, 0)
        };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Naranja, 3);
            e.Graphics.DrawLine(pen, 22, header.Height - 4, header.Width - 22, header.Height - 4);
        };
        header.Controls.Add(new Label
        {
            Text = "Asistente de instalación",
            ForeColor = Color.White,
            Font = new Font("Figtree", 16F, FontStyle.Bold),
            Location = new Point(22, 12),
            AutoSize = true
        });

        // ===================== Indicador de pasos =====================
        var pasoBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            BackColor = Color.White,
            Padding = new Padding(22, 12, 22, 0)
        };
        pasoBar.Paint += (_, e) =>
        {
            using var pen = new Pen(Borde, 1);
            e.Graphics.DrawLine(pen, 0, pasoBar.Height - 1, pasoBar.Width, pasoBar.Height - 1);
        };
        string[] nombresPasos = { "Ubicación", "BD y credenciales", "Instalar y auditar", "Finalizar" };
        int x = 22;
        for (int i = 0; i < 4; i++)
        {
            var lbl = new Label
            {
                Text = $"{(i + 1)}. {nombresPasos[i]}",
                AutoSize = false,
                Size = new Size(175, 34),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Figtree", 9.5F, FontStyle.Bold),
                ForeColor = TextoMuted,
                BackColor = Fondo,
                FlatStyle = FlatStyle.Flat,
                AutoEllipsis = true // "3. Instalar y auditar" no se corta
            };
            lbl.Paint += (_, e) =>
            {
                using var pen = new Pen(Borde, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, lbl.Width - 1, lbl.Height - 1);
            };
            lbl.Location = new Point(x, 6);
            x += 178;
            pasoBar.Controls.Add(lbl);
            _pasosIndicador[i] = lbl;
        }

        // ===================== Paneles de contenido =====================
        var contenido = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Fondo,
            Padding = new Padding(24, 20, 24, 10)
        };

        _paneles[0] = CrearPanelUbicacion();
        _paneles[1] = CrearPanelBd();
        _paneles[2] = CrearPanelInstalar();
        _paneles[3] = CrearPanelFinalizar();
        foreach (var p in _paneles)
            contenido.Controls.Add(p);

        // ===================== Pie (navegacion) =====================
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            BackColor = Color.White,
            Padding = new Padding(22, 10, 22, 10)
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Borde, 1);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        _btnAtras = CrearBoton("←  Atrás", Color.White, AzulMarino, Borde);
        _btnSiguiente = CrearBoton("Siguiente  →", AzulMarino, Color.White, null);
        _btnFinalizar = CrearBoton("✓  Finalizar", Naranja, AzulMarino, null);
        _btnFinalizar.Visible = false;

        _btnAtras.Click += (_, _) => IrAPaso(_paso - 1);
        _btnSiguiente.Click += (_, _) => Avanzar();
        _btnFinalizar.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };

        footer.Controls.Add(_btnAtras);
        footer.Controls.Add(_btnSiguiente);
        footer.Controls.Add(_btnFinalizar);

        // Posicionar los botones del pie: Atras a la izquierda, Siguiente/Finalizar
        // a la derecha, respetando el padding (22, 10, 22, 10) y el ancho del boton
        // (140). Sin esto, los tres quedan apilados en (0,0) y se tapan entre si
        // (en el paso 2, "Atras" ocultaba al boton "Siguiente").
        footer.Layout += (_, _) =>
        {
            _btnAtras.Location = new Point(22, 11);
            int xDerecha = footer.ClientSize.Width - 22 - 140;
            _btnSiguiente.Location = new Point(xDerecha, 11);
            _btnFinalizar.Location = new Point(xDerecha, 11);
        };

        // ===================== Ensamblar =====================
        Controls.Add(contenido);
        Controls.Add(footer);
        Controls.Add(pasoBar);
        Controls.Add(header);

        _txtRaiz.Text = _raiz;
        _btnSiguiente.Enabled = true; // paso 1 siempre valido
        IrAPaso(1);
    }

    // ==================================================================
    // PANELES POR PASO
    // ==================================================================

    private Panel CrearPanelUbicacion()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Fondo };

        p.Controls.Add(Titulo("¿Dónde quieres instalar el sincronizador?",
            "Se creará una carpeta con todo lo que el sincronizador necesita, independientemente de dónde se clone el repo."));

        p.Controls.Add(Lbl("Carpeta de instalación", 78));
        _txtRaiz = new TextBox
        {
            Location = new Point(0, 100),
            Width = 640,
            Font = new Font("Cascadia Code", 10F),
            BorderStyle = BorderStyle.FixedSingle
        };
        var btnExaminar = CrearBoton("Examinar…", AzulMarino, Color.White, null);
        btnExaminar.Size = new Size(120, 34);
        btnExaminar.Location = new Point(656, 96);
        btnExaminar.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Selecciona la carpeta donde se instalará el sincronizador",
                SelectedPath = _raiz
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtRaiz.Text = dlg.SelectedPath;
        };

        p.Controls.Add(_txtRaiz);
        p.Controls.Add(btnExaminar);

        p.Controls.Add(Lbl("Estructura que se creará:", 155));
        _lblEstructura = new Label
        {
            Location = new Point(0, 178),
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Font = new Font("Cascadia Code", 9F),
            ForeColor = TextoMuted
        };
        ActualizarEstructura();
        _txtRaiz.TextChanged += (_, _) => ActualizarEstructura();
        p.Controls.Add(_lblEstructura);

        var nota = new Label
        {
            Location = new Point(0, 310),
            AutoSize = true,
            Font = new Font("Figtree", 9F, FontStyle.Regular),
            ForeColor = TextoMuted,
            Text = "El launcher (Rutx.Sincronizador.Admin.exe) arranca el sync con esta\n"
                 + "carpeta como directorio de trabajo: la config y los datos caen ahí,\n"
                 + "sin depender de dónde se haya clonado el repo."
        };

        p.Controls.Add(nota);
        return p;
    }

    private void ActualizarEstructura()
    {
        var raiz = _txtRaiz.Text.Trim();
        if (string.IsNullOrWhiteSpace(raiz))
            raiz = InstalacionHelper.RutaDefault;
        _lblEstructura.Text = "Destino: " + raiz + "\\"
            + "\n└─ Estructura que se creará:"
            + "\n    ├─ Rutx.Sincronizador.exe + DLLs (raíz plana)"
            + "\n    ├─ wwwroot\\        → panel web /admin"
            + "\n    ├─ appsettings.json → configuración protegida con ACLs"
            + "\n    ├─ Logs\\           → bitácora de ejecución"
            + "\n    ├─ backups\\        → respaldos web"
            + "\n    └─ instalacion.json → marcador de estado"
            + "\n\n* La BD complementaria y fotos se ubican en C:\\Microsip Extras\\";
    }

    private Panel CrearPanelBd()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Fondo };

        p.Controls.Add(Titulo("Selecciona la base de datos de Microsip",
            "Se verificará que el archivo .fdb exista y que las credenciales conecten. Solo lectura: nada se modifica."));

        p.Controls.Add(Lbl("Archivo de base de datos (.fdb)", 70));
        _txtFdb = new TextBox
        {
            Text = _rutaFdb,
            Location = new Point(0, 92),
            Width = 640,
            Font = new Font("Cascadia Code", 10F),
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true
        };
        var btnBuscar = CrearBoton("Buscar BD…", AzulMarino, Color.White, null);
        btnBuscar.Size = new Size(120, 32);
        btnBuscar.Location = new Point(656, 88);
        btnBuscar.Click += async (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Base de datos Firebird|*.fdb;*.FDB|Todos|*.*",
                Title = "Selecciona la BD de Microsip (ej: CHOCOLATES.fdb)"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _txtFdb.Text = dlg.FileName;
            _rutaFdb = dlg.FileName;
            _conexionOk = false;
            _lblConexion.Text = "Verificando…";
            _lblConexion.ForeColor = AmarilloWarn;
            _btnSiguiente.Enabled = false;

            try
            {
                // Verificar firma y probar credenciales configuradas o pedir nuevas
                await ProbarConexionAsync(_txtUsuario.Text.Trim(), _txtPassword.Text);
            }
            catch (Exception ex)
            {
                MostrarErrorConexion("Error inesperado: " + ex.Message);
            }
        };

        p.Controls.Add(_txtFdb);
        p.Controls.Add(btnBuscar);

        p.Controls.Add(Lbl("Usuario Firebird", 135));
        _txtUsuario = new TextBox
        {
            Text = _usuario,
            Location = new Point(0, 155),
            Width = 280,
            Font = new Font("Cascadia Code", 10F),
            BorderStyle = BorderStyle.FixedSingle
        };
        p.Controls.Add(_txtUsuario);

        p.Controls.Add(Lbl("Contraseña Firebird", 195));
        _txtPassword = new TextBox
        {
            Text = "",
            Location = new Point(0, 215),
            Width = 280,
            Font = new Font("Cascadia Code", 10F),
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = true
        };
        p.Controls.Add(_txtPassword);

        var lblWebPass = Lbl("Contraseña Panel Web (mín. 8 caracteres)", 195);
        lblWebPass.Location = new Point(320, 195);
        p.Controls.Add(lblWebPass);
        _txtWebPassword = new TextBox
        {
            Text = "",
            Location = new Point(320, 215),
            Width = 280,
            Font = new Font("Cascadia Code", 10F),
            BorderStyle = BorderStyle.FixedSingle,
            UseSystemPasswordChar = true
        };
        p.Controls.Add(_txtWebPassword);

        _btnProbar = CrearBoton("Probar conexión Firebird", Naranja, AzulMarino, null);
        _btnProbar.Size = new Size(200, 34);
        _btnProbar.Location = new Point(0, 260);
        _btnProbar.Click += async (_, _) =>
        {
            try
            {
                _conexionOk = false;
                _btnSiguiente.Enabled = false;
                _lblConexion.Text = "Probando…";
                _lblConexion.ForeColor = AmarilloWarn;
                await ProbarConexionAsync(_txtUsuario.Text.Trim(), _txtPassword.Text);
            }
            catch (Exception ex)
            {
                MostrarErrorConexion("Error inesperado: " + ex.Message);
            }
        };
        p.Controls.Add(_btnProbar);

        _spinnerConexion.Location = new Point(0, 305);
        _spinnerConexion.Visible = false;
        p.Controls.Add(_spinnerConexion);

        _lblConexion = new Label
        {
            Location = new Point(30, 303),
            AutoSize = true,
            Font = new Font("Figtree", 10F, FontStyle.Bold),
            ForeColor = TextoMuted,
            Text = "Selecciona primero la BD y prueba conexión."
        };
        p.Controls.Add(_lblConexion);

        p.Controls.Add(new Label
        {
            Location = new Point(210, 264),
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            Font = new Font("Figtree", 8.5F, FontStyle.Regular),
            ForeColor = TextoMuted,
            Text = "El panel web se creará con usuario 'admin' y la contraseña indicada (mín. 8 caracteres; se pedirá cambio al primer acceso)."
        });
        return p;
    }

    private Panel CrearPanelInstalar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Fondo };

        p.Controls.Add(Titulo("Instalar y auditar la BD",
            "Se copiarán los ejecutables, se generará la configuración con tu BD y se ejecutará la auditoría de compatibilidad en vivo."));

        _btnInstalar = CrearBoton("▶  Instalar ahora", AzulMarino, Color.White, null);
        _btnInstalar.Size = new Size(190, 44);
        _btnInstalar.Location = new Point(0, 88);
        _btnInstalar.Click += async (_, _) => await InstalarAsync();
        p.Controls.Add(_btnInstalar);

        _chkAccesoMovil = new CheckBox
        {
            Text = "Habilitar acceso remoto público (Cloudflare Tunnel)",
            Location = new Point(0, 140),
            AutoSize = true,
            Font = new Font("Figtree", 10F, FontStyle.Regular),
            ForeColor = AzulMarino,
            Cursor = Cursors.Hand
        };
        _chkAccesoMovil.CheckedChanged += (_, _) => ActualizarHostnameVisibilidad();
        p.Controls.Add(_chkAccesoMovil);

        _lblHostname = Lbl("Hostname público (FQDN de Cloudflare, ej: sync.cliente.com)", 168);
        p.Controls.Add(_lblHostname);
        _txtHostname = new TextBox
        {
            Text = "",
            Location = new Point(0, 188),
            Width = 560,
            Font = new Font("Cascadia Code", 10F),
            BorderStyle = BorderStyle.FixedSingle
        };
        p.Controls.Add(_txtHostname);

        ActualizarHostnameVisibilidad();

        _spinnerInstalar.Location = new Point(0, 228);
        _spinnerInstalar.Visible = false;
        p.Controls.Add(_spinnerInstalar);

        _lblEstadoInstalacion = new Label
        {
            Location = new Point(30, 226),
            AutoSize = true,
            Font = new Font("Figtree", 10F, FontStyle.Bold),
            ForeColor = AzulMarino,
            Text = "Instalando y auditando… esto puede tardar unos segundos.",
            Visible = false
        };
        p.Controls.Add(_lblEstadoInstalacion);

        _lblContadores = new Label
        {
            Location = new Point(0, 224),
            AutoSize = true,
            Font = new Font("Cascadia Code", 13F, FontStyle.Bold),
            ForeColor = TextoMuted,
            Text = ""
        };
        p.Controls.Add(_lblContadores);

        _lblSugerencia = new Label
        {
            Location = new Point(0, 254),
            AutoSize = true,
            Font = new Font("Figtree", 9.5F, FontStyle.Regular),
            ForeColor = Rojo,
            Text = "Hay configuraciones pendientes (🔴). Ábrelas desde el panel web (⚙ Conf) o revisa el manual, sección 8.",
            Visible = false
        };
        p.Controls.Add(_lblSugerencia);

        _txtProgreso = new RichTextBox
        {
            Location = new Point(0, 276),
            Size = new Size(770, 180),
            BackColor = ConsolaFondo,
            ForeColor = ConsolaTexto,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Cascadia Code", 9F),
            DetectUrls = false
        };
        p.Controls.Add(_txtProgreso);

        // La consola ocupa todo el espacio restante del panel y se reajusta al
        // redimensionar. Antes tenia altura fija (270) que se salia del panel y
        // su parte inferior quedaba tapada por el pie.
        p.Layout += (_, _) =>
        {
            var alto = Math.Max(120, p.ClientSize.Height - 280);
            _txtProgreso.SetBounds(0, 276, Math.Max(p.ClientSize.Width, 300), alto);
        };
        return p;
    }

    private Panel CrearPanelFinalizar()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Fondo };

        p.Controls.Add(Titulo("Instalación completada",
            "El sincronizador quedó instalado y configurado. Estos son los datos:"));

        _lblResumen = new Label
        {
            Location = new Point(0, 100),
            AutoSize = true,
            Font = new Font("Cascadia Code", 10.5F),
            ForeColor = AzulMarino
        };
        p.Controls.Add(_lblResumen);

        p.Controls.Add(new Label
        {
            Location = new Point(0, 260),
            AutoSize = true,
            Font = new Font("Figtree", 10F, FontStyle.Regular),
            ForeColor = TextoMuted,
            Text = "Para terminar:\n"
                 + "  1. Usa el acceso directo \"RUTX Sincronizador\" en tu Escritorio (o presiona ▶ Iniciar).\n"
                 + "  2. Abre el panel web con ⚙ Conf (web) para ajustar IDs o ver la auditoría\n"
                 + "     con los puntitos de estado (🔴 configurar · 🟡 revisar · 🟢 ok)."
        });
        return p;
    }

    // ==================================================================
    // LOGICA DEL WIZARD
    // ==================================================================

    private void IrAPaso(int paso)
    {
        _paso = paso;
        for (int i = 0; i < 4; i++)
        {
            _paneles[i].Visible = (i + 1) == paso;
            var lbl = _pasosIndicador[i];
            if (i + 1 < paso)
            {
                lbl.BackColor = Color.FromArgb(0xDC, 0xFC, 0xE7);
                lbl.ForeColor = Color.FromArgb(0x16, 0x6B, 0x34);
                lbl.Text = $"✓ {nombresPaso(i)}";
            }
            else if (i + 1 == paso)
            {
                lbl.BackColor = AzulMarino;
                lbl.ForeColor = Color.White;
            }
            else
            {
                lbl.BackColor = Fondo;
                lbl.ForeColor = TextoMuted;
                lbl.Text = $"{i + 1}. {nombresPaso(i)}";
            }
        }

        _btnAtras.Visible = paso > 1;
        bool esUltimo = paso == 4;
        _btnSiguiente.Visible = !esUltimo;
        _btnFinalizar.Visible = esUltimo;

        if (paso == 3)
        {
            _btnSiguiente.Enabled = _instalado;
            _lblContadores.Text = _instalado
                ? $"  🔴 {_nFallos} faltantes    🟡 {_nAvisos} avisos    🟢 {_nOk} ok"
                : "";
            _lblSugerencia.Visible = false;
        }
        if (paso == 4)
            _lblResumen.Text = ResumenTexto();
    }

    private void ActualizarHostnameVisibilidad()
    {
        var visible = _chkAccesoMovil.Checked;
        if (_lblHostname != null) _lblHostname.Visible = visible;
        if (_txtHostname != null)
        {
            _txtHostname.Visible = visible;
            if (!visible) _txtHostname.Clear();
        }
    }

    private static string nombresPaso(int i) => new[] { "Ubicación", "BD y credenciales", "Instalar y auditar", "Finalizar" }[i];

    private void Avanzar()
    {
        switch (_paso)
        {
            case 1:
                // Validar carpeta destino
                _raiz = _txtRaiz.Text.Trim();
                if (string.IsNullOrWhiteSpace(_raiz))
                {
                    MessageBox.Show("Indica la carpeta de instalación.", "RUTX · Instalación",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try
                {
                    Directory.CreateDirectory(_raiz);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("No se puede crear la carpeta:\n" + ex.Message, "RUTX · Instalación",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                IrAPaso(2);
                break;

            case 2:
                if (!_conexionOk)
                {
                    MessageBox.Show("Primero confirma que la conexión a la BD sea correcta.", "RUTX · Instalación",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var webPass = _txtWebPassword.Text;
                var panelUser = Rutx.Sincronizador.Shared.WebPasswordPolicy.UsuarioPanelDefault;
                if (!Rutx.Sincronizador.Shared.WebPasswordPolicy.EsValida(webPass, panelUser, out var passError))
                {
                    MessageBox.Show(passError, "RUTX · Instalación",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _webPassword = webPass.Trim();
                IrAPaso(3);
                break;

            case 3:
                if (_instalado) IrAPaso(4);
                break;
        }
    }

    private async Task ProbarConexionAsync(string usuario, string password)
    {
        if (string.IsNullOrWhiteSpace(_rutaFdb))
        {
            _lblConexion.Text = "Selecciona primero la BD.";
            _lblConexion.ForeColor = TextoMuted;
            return;
        }

        _btnProbar.Enabled = false;
        _spinnerConexion.Visible = true;
        _spinnerConexion.Girar(true);
        try
        {
            var tarea = Task.Run(() =>
            {
                if (!FbConexionHelper.ArchivoFdbValido(_rutaFdb, out var msj))
                    return (false, msj, (FirebirdVersionInfo?)null);
                var ok = FbConexionHelper.ProbarConexion(_rutaFdb, usuario, password, out var m, out var vInfo);
                return (ok, m, vInfo);
            });

            // Timeout duro: si el cliente Firebird se cuelga, nunca dejamos al
            // usuario sin respuesta — siempre se muestra un mensaje claro.
            // 25s: da margen a Firebird 2.x/1.x y a BDs grandes en PCs lentos.
            var completada = await Task.WhenAny(tarea, Task.Delay(TimeSpan.FromSeconds(25)));
            if (completada != tarea)
            {
                _lblConexion.Text = "🔴 El servidor tardo demasiado en responder. Verifica que Firebird este en ejecucion y que la ruta de la BD sea correcta.";
                _lblConexion.ForeColor = Rojo;
                _btnSiguiente.Enabled = false;
                return;
            }

            var (ok, mensaje, vInfo) = await tarea;

            _conexionOk = ok;
            _versionInfo = vInfo;
            if (ok)
            {
                // IMPORTANTE: guardar las credenciales que SÍ conectaron para
                // escribirlas en appsettings.json durante la instalacion (no
                // usar los defaults si el usuario las cambio).
                _usuario = usuario;
                _password = password;
            }
            _lblConexion.Text = ok ? "🟢 " + mensaje : "🔴 " + mensaje;
            _lblConexion.ForeColor = ok ? Color.FromArgb(0x16, 0x8A, 0x34) : Rojo;
            _btnSiguiente.Enabled = ok;
        }
        finally
        {
            _btnProbar.Enabled = true;
            _spinnerConexion.Girar(false);
            _spinnerConexion.Visible = false;
        }
    }

    private void MostrarErrorConexion(string mensaje)
    {
        _spinnerConexion.Girar(false);
        _spinnerConexion.Visible = false;
        _btnProbar.Enabled = true;
        _lblConexion.Text = "🔴 " + mensaje;
        _lblConexion.ForeColor = Rojo;
        _btnSiguiente.Enabled = false;
    }

    private async Task InstalarAsync()
    {
        _btnInstalar.Enabled = false;
        _btnAtras.Enabled = false;
        _txtProgreso.Clear();
        _spinnerInstalar.Visible = true;
        _spinnerInstalar.Girar(true);
        _lblEstadoInstalacion.Visible = true;
        Log("Iniciando instalación…", "inf");
        Log("Origen (build del sync): " + _carpetaFuenteSync, "inf");

        bool esFreshInstall = !Directory.Exists(_raiz);
        InstalacionInfo? infoGenerada = null;

        try
        {
            // 1) Verificar puerto 5047 libre de forma robusta
            bool puertoLibre = true;
            try
            {
                var propiedades = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                var listeners = propiedades.GetActiveTcpListeners();
                var conexiones = propiedades.GetActiveTcpConnections();
                
                if (listeners.Any(l => l.Port == 5047) || conexiones.Any(c => c.LocalEndPoint.Port == 5047))
                {
                    puertoLibre = false;
                }
            }
            catch { /* ignorar */ }

            if (!puertoLibre)
            {
                Log("AVISO: el puerto 5047 ya está ocupado. Intenta cerrar cualquier instancia previa (revisa netstat -ano).", "warn");
                throw new InvalidOperationException("El puerto 5047 está ocupado. Cierra la instancia anterior y reintenta.");
            }

            // 2) Instalar (estructura + copia + appsettings) sin marcador.
            //    El hostname se valida aqui (ademas del helper) para abortar
            //    con un mensaje claro antes de tocar el disco si falta.
            Log("Creando estructura y copiando ejecutables…", "inf");
            bool mobileAcc = _chkAccesoMovil.Checked;
            var hostnameSaneado = Rutx.Sincronizador.Shared.CloudflareHostnamePolicy.Sanitizar(_txtHostname.Text.Trim());
            if (mobileAcc &&
                !Rutx.Sincronizador.Shared.CloudflareHostnamePolicy.EsFqdnValido(hostnameSaneado, out var hostError))
            {
                throw new InvalidOperationException(hostError);
            }
            infoGenerada = await Task.Run(() =>
                InstalacionHelper.Instalar(_raiz, _rutaFdb, _usuario, _password, _carpetaFuenteSync, mobileAcc, _webPassword, hostnameSaneado));

            Log("Ejecutables copiados en: " + _raiz, "inf");
            Log("appsettings.json generado con tu BD (" + _rutaFdb + ")", "inf");
            if (mobileAcc) Log("Configurado acceso remoto publico via Cloudflare Tunnel (listener 127.0.0.1:5048).", "inf");

            // 3) Auditar en vivo (ya que verificamos puerto libre)
            Log("Arrancando el sincronizador para validación y auditoría…", "inf");
            using var sync = new SyncProcessController();
            sync.LogLine += (_, l) => Log(l, NivelDe(l));
            sync.Iniciar(infoGenerada.ExeSync, infoGenerada.Raiz);

            var (apiOk, razonApi) = await EsperarApiAsync(sync);
            if (apiOk)
            {
                Log("API lista. Ejecutando auditoría de compatibilidad…", "inf");
                _auditoriaOk = await EjecutarAuditoriaAsync();
            }
            else
            {
                sync.Detener();
                throw new InvalidOperationException(razonApi);
            }

            sync.Detener();
            Log("Sincronizador detenido (validación exitosa).", "inf");

            // 4) Escribir marcador como contrato final
            var rutaMarcador = Path.Combine(_raiz, InstalacionHelper.NombreArchivoMarcador);
            File.WriteAllText(rutaMarcador, JsonSerializer.Serialize(infoGenerada, new JsonSerializerOptions { WriteIndented = true }));
            Log("Marcador de instalación creado exitosamente.", "inf");

            // Crear acceso directo en el Escritorio
            try
            {
                InstalacionHelper.CrearAccesoDirectoEscritorio(infoGenerada.ExeAdmin);
                Log("Acceso directo creado en el Escritorio: \"RUTX Sincronizador\".", "inf");
            }
            catch (Exception ex)
            {
                Log("No se pudo crear el acceso directo: " + ex.Message, "warn");
            }

            _instalado = true;
            _onInstalada?.Invoke(infoGenerada.ExeSync, infoGenerada.Raiz);

            // Ocultar el indicador de carga
            _spinnerInstalar.Girar(false);
            _spinnerInstalar.Visible = false;
            _lblEstadoInstalacion.Visible = false;

            _lblContadores.Text = _auditoriaOk
                ? $"  🔴 {_nFallos} faltantes    🟡 {_nAvisos} avisos    🟢 {_nOk} ok"
                : "  Auditoría fallida — desde el panel web (⚙ Conf) se re-evalúa al abrir.";
            _lblSugerencia.Visible = _auditoriaOk && _nFallos > 0;

            Log("Instalación completada. Cerrando asistente…", "inf");
            await Task.Delay(1000);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Log("ERROR de instalación: " + ex.Message, "err");

            // Rollback
            try
            {
                if (esFreshInstall && Directory.Exists(_raiz))
                {
                    Log("Limpiando carpeta de instalación fallida...", "warn");
                    Directory.Delete(_raiz, true);
                    Log("Carpeta limpiada exitosamente. (Los respaldos en C:\\Microsip Extras\\Respaldos se conservan).", "inf");
                }
                else if (Directory.Exists(_raiz))
                {
                    Log("Marcando instalación como fallida (instalacion.error.json)...", "warn");
                    File.WriteAllText(Path.Combine(_raiz, "instalacion.error.json"), $"Error: {ex.Message}\n{ex.StackTrace}");
                    var marcadorCorrecto = Path.Combine(_raiz, InstalacionHelper.NombreArchivoMarcador);
                    if (File.Exists(marcadorCorrecto)) File.Delete(marcadorCorrecto);
                }
            }
            catch (Exception rEx)
            {
                Log("ERROR al intentar revertir la instalación: " + rEx.Message, "err");
            }

            MessageBox.Show("La instalación falló:\n" + ex.Message + "\n\nCualquier respaldo (C:\\Microsip Extras\\Respaldos) se conserva intacto.", "RUTX · Instalación",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnInstalar.Enabled = true;
            _btnAtras.Enabled = true;
            if (!_instalado)
            {
                _spinnerInstalar.Girar(false);
                _spinnerInstalar.Visible = false;
                _lblEstadoInstalacion.Visible = false;
                _btnSiguiente.Enabled = false;
            }
        }
    }

    private async Task<(bool ok, string razon)> EsperarApiAsync(SyncProcessController sync)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (int i = 0; i < 50; i++)
        {
            if (!sync.IsRunning)
                return (false, "El proceso del sincronizador terminó inesperadamente. Revisa los logs en rojo arriba (error de conexión a la BD u otro fallo al arrancar).");

            try
            {
                var resp = await http.GetAsync("http://localhost:5047/health");
                if (resp.IsSuccessStatusCode) return (true, "ok");
            }
            catch { /* aun no levanta */ }

            await Task.Delay(i < 10 ? 200 : 500);
        }
        return (false, "La API no respondió en el tiempo esperado (15 s). Si Firebird no está en ejecución, inícialo primero.");
    }

    private async Task<bool> EjecutarAuditoriaAsync()
    {
        try
        {
            using var resp = await _http.PostAsync(
                "http://localhost:5047/api/v2/admin/auditoria", null);
            if (!resp.IsSuccessStatusCode)
            {
                Log($"ERROR: la auditoría respondió HTTP {(int)resp.StatusCode}.", "err");
                return false;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var raiz = doc.RootElement;
            _nOk = raiz.TryGetProperty("ok", out var v1) ? v1.GetInt32() : 0;
            _nAvisos = raiz.TryGetProperty("avisos", out var v2) ? v2.GetInt32() : 0;
            _nFallos = raiz.TryGetProperty("fallos", out var v3) ? v3.GetInt32() : 0;

            Log($"Auditoría: OK={_nOk} | AVISOS={_nAvisos} | FALLOS={_nFallos}", _nFallos > 0 ? "warn" : "inf");

            if (raiz.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var estado = item.TryGetProperty("estado", out var e) ? e.GetString() : "";
                    var texto = $"{SeccionDe(item)} | {ItemDe(item)} | {MensajeDe(item)}";
                    Log(texto, estado switch
                    {
                        "fallo" => "err",
                        "aviso" => "warn",
                        _ => "inf"
                    });
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Log("ERROR al ejecutar la auditoría: " + ex.Message, "err");
            return false;
        }
    }

    private static string SeccionDe(JsonElement item) =>
        item.TryGetProperty("seccion", out var v) ? v.GetString() ?? "" : "";
    private static string ItemDe(JsonElement item) =>
        item.TryGetProperty("item", out var v) ? v.GetString() ?? "" : "";
    private static string MensajeDe(JsonElement item) =>
        item.TryGetProperty("mensaje", out var v) ? v.GetString() ?? "" : "";

    private string ResumenTexto()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Carpeta de instalación : {_raiz}");
        sb.AppendLine($"Base de datos           : {_rutaFdb}");
        sb.AppendLine($"Usuario Firebird        : {_usuario}");
        sb.AppendLine($"Panel web               : usuario='admin' | contraseña='{_webPassword}' (cambio obligatorio al ingresar)");
        sb.AppendLine();
        sb.AppendLine(_auditoriaOk
            ? $"Resultado de la auditoría :  🔴 {_nFallos} faltantes   🟡 {_nAvisos} avisos   🟢 {_nOk} ok"
            : "Auditoría : pendiente (ejecútala desde el panel web)");
        sb.AppendLine("Acceso directo    : \"RUTX Sincronizador\" en el Escritorio");
        return sb.ToString();
    }

    // ==================================================================
    // HELPERS DE UI
    // ==================================================================

    private static Panel Titulo(string titulo, string sub)
    {
        var panel = new Panel { Location = new Point(0, 0), Size = new Size(780, 66) };
        panel.Controls.Add(new Label
        {
            Text = titulo,
            Font = new Font("Figtree", 13F, FontStyle.Bold),
            ForeColor = AzulMarino,
            AutoSize = true,
            Location = new Point(0, 0)
        });
        panel.Controls.Add(new Label
        {
            Text = sub,
            Font = new Font("Figtree", 9.5F, FontStyle.Regular),
            ForeColor = TextoMuted,
            AutoSize = true,
            Location = new Point(0, 28),
            MaximumSize = new Size(780, 0)
        });
        return panel;
    }

    private static Label Lbl(string texto, int y)
    {
        return new Label
        {
            Text = texto,
            AutoSize = true,
            Font = new Font("Figtree", 9.5F, FontStyle.Bold),
            ForeColor = TextoMuted,
            Location = new Point(0, y)
        };
    }

    private static Button CrearBoton(string texto, Color fondo, Color frente, Color? borde)
    {
        var btn = new Button
        {
            Text = texto,
            BackColor = fondo,
            ForeColor = frente,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = borde.HasValue ? 1 : 0 },
            Size = new Size(140, 40),
            Font = new Font("Figtree", 10F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        if (borde.HasValue)
            btn.FlatAppearance.BorderColor = borde.Value;
        return btn;
    }

    private static string NivelDe(string linea)
    {
        var l = linea.ToLowerInvariant();
        if (l.Contains("error") || l.Contains("fail") || l.Contains("fatal") || l.Contains("exception"))
            return "err";
        if (l.Contains("aviso") || l.Contains("warn"))
            return "warn";
        return "inf";
    }

    private void Log(string linea, string nivel)
    {
        if (_txtProgreso.IsDisposed) return;
        if (_txtProgreso.InvokeRequired)
        {
            _txtProgreso.BeginInvoke(() => Log(linea, nivel));
            return;
        }

        var color = nivel switch
        {
            "warn" => AmarilloWarn,
            "err" => RojoError,
            _ => VerdeInfo
        };

        _txtProgreso.SelectionStart = _txtProgreso.TextLength;
        _txtProgreso.SelectionColor = color;
        _txtProgreso.SelectionFont = new Font(_txtProgreso.Font.FontFamily, _txtProgreso.Font.Size,
            nivel == "inf" ? FontStyle.Regular : FontStyle.Bold);
        _txtProgreso.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + linea + "\n");
        _txtProgreso.SelectionColor = ConsolaTexto;
        _txtProgreso.ScrollToCaret();
    }

    // ==================================================================
    // CONTROL: circulo de carga (spinner) pintado a mano
    // ==================================================================

    private sealed class SpinnerCircular : Control
    {
        private readonly System.Windows.Forms.Timer _timer;
        private float _angulo;

        public SpinnerCircular()
        {
            Size = new Size(22, 22);
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            _timer = new System.Windows.Forms.Timer { Interval = 16 };
            _timer.Tick += (_, _) =>
            {
                _angulo = (_angulo + 6) % 360;
                Invalidate();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        public void Girar(bool activo)
        {
            _timer.Enabled = activo;
            if (activo) Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(AzulMarino, 3)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawArc(pen, 3, 3, Width - 8, Height - 8, _angulo, 300);
        }
    }
}
