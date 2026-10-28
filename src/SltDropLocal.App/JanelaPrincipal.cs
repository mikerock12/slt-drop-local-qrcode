using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Reflection;
using SltDropLocal.Core.Envios;
using SltDropLocal.Core.Qr;
using SltDropLocal.Core.Rede;
using SltDropLocal.Core.Servidor;
using SltDropLocal.Core.Sessoes;

namespace SltDropLocal.App;

internal sealed class JanelaPrincipal : Form
{
    // Paleta da Smells Like Tech (a mesma da página do celular).
    private static readonly Color Fundo = Color.FromArgb(0x12, 0x13, 0x16);
    private static readonly Color Cartao = Color.FromArgb(0x1C, 0x1D, 0x21);
    private static readonly Color Borda = Color.FromArgb(0x2C, 0x2D, 0x33);
    private static readonly Color Texto = Color.FromArgb(0xF2, 0xF2, 0xF3);
    private static readonly Color Suave = Color.FromArgb(0xB4, 0xB4, 0xBB);
    private static readonly Color Laranja = Color.FromArgb(0xFF, 0x7A, 0x1A);
    private static readonly Color Verde = Color.FromArgb(0x3E, 0xCF, 0x7A);
    private static readonly Color Vermelho = Color.FromArgb(0xFF, 0x5A, 0x5F);
    private static readonly Color Amarelo = Color.FromArgb(0xF5, 0xC5, 0x42);

    private readonly Opcoes _opcoes;
    private readonly Configuracoes _config;
    private readonly GerenciadorDeSessoes _sessoes;
    private readonly GerenciadorDeEnvios _envios;
    private readonly ServidorDrop _servidor;
    private readonly System.Windows.Forms.Timer _relogio = new() { Interval = 500 };
    private readonly EventWaitHandle _pedidoMostrar = new(false, EventResetMode.AutoReset, Program.NomeEventoMostrar);
    private readonly RegisteredWaitHandle _esperaMostrar;

    private readonly PictureBox _qr = new();
    private readonly Label _qrAviso = new();
    private readonly Label _codigo = new();
    private readonly TextBox _url = new();
    private readonly Label _conexao = new();
    private readonly Label _expiracao = new();
    private readonly Label _pasta = new();
    private readonly ComboBox _rede = new();
    private readonly Label _aviso = new();
    private readonly ListView _lista = new();
    private readonly Label _rodape = new();
    private readonly ToolTip _dica = new();

    private bool _servidorPronto;
    private string? _urlAtual;
    private string? _conteudoQr;
    private bool _qrDoCodigo;
    private readonly Label _tituloQr = new();
    private readonly Panel _molduraQr = new();
    private Button _botaoQrCodigo = null!;
    private string? _mensagemTroca;
    private DateTime _mensagemTrocaAte;
    private bool _fechando;

    public JanelaPrincipal(string pasta, Opcoes opcoes, Configuracoes config)
    {
        _opcoes = opcoes;
        _config = config;
        // O histórico em %APPDATA% garante que um código de acesso nunca se repita, nem entre execuções.
        var historico = new HistoricoDeCodigos(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Program.NomeApp, "codigos-usados.txt"));
        _sessoes = new GerenciadorDeSessoes(limiteInatividade: opcoes.Inatividade, historico: historico);
        _envios = new GerenciadorDeEnvios(pasta);
        _servidor = new ServidorDrop(_sessoes, _envios, new OpcoesServidor { PortaPreferida = opcoes.Porta ?? 8770 });
        _sessoes.SessaoTrocada += AoTrocarSessao;

        MontarTela();
        AtualizarPasta();
        CarregarEnderecos();

        _relogio.Tick += (_, _) => Atualizar();
        NetworkChange.NetworkAddressChanged += AoMudarRede;
        _esperaMostrar = ThreadPool.RegisterWaitForSingleObject(_pedidoMostrar,
            (_, _) => { if (IsHandleCreated) BeginInvoke(TrazerParaFrente); }, null, Timeout.Infinite, false);
    }

    // ---------------------------------------------------------------- tela

    private void MontarTela()
    {
        Text = Program.NomeApp;
        Icon = CarregarIcone();
        BackColor = Fundo;
        ForeColor = Texto;
        Font = new Font("Segoe UI", 9.75f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1020, 720);
        MinimumSize = new Size(900, 680);

        var raiz = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(18, 12, 18, 8), BackColor = Fundo,
        };
        raiz.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
        raiz.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        raiz.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        raiz.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Cabeçalho
        var cabecalho = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0, 0, 0, 10), WrapContents = false };
        cabecalho.Controls.Add(new Label { Text = Program.NomeApp, AutoSize = true, Font = new Font("Segoe UI Semibold", 16f), ForeColor = Texto, Margin = new Padding(0) });
        cabecalho.Controls.Add(new Label { Text = "Envie arquivos do celular para este PC pela rede Wi-Fi/LAN, sem cabo e sem internet.", AutoSize = true, ForeColor = Suave, Margin = new Padding(1, 2, 0, 0) });
        raiz.Controls.Add(cabecalho, 0, 0);
        raiz.SetColumnSpan(cabecalho, 2);

        raiz.Controls.Add(MontarColunaQr(), 0, 1);
        raiz.Controls.Add(MontarColunaTransferencias(), 1, 1);

        _rodape.Dock = DockStyle.Bottom;
        _rodape.Height = 26;
        _rodape.ForeColor = Suave;
        _rodape.BackColor = Cartao;
        _rodape.Padding = new Padding(18, 0, 18, 0);
        _rodape.TextAlign = ContentAlignment.MiddleLeft;
        _rodape.Text = $"Versão {Versao()} · Smells Like Tech";

        Controls.Add(raiz);
        Controls.Add(_rodape);
    }

    private Control MontarColunaQr()
    {
        var coluna = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = Cartao, Padding = new Padding(18), Margin = new Padding(0, 0, 14, 0) };
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coluna.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _tituloQr.AutoSize = true;
        _tituloQr.MaximumSize = new Size(360, 0);
        _tituloQr.Font = new Font("Segoe UI Semibold", 11f);
        _tituloQr.ForeColor = Texto;
        _tituloQr.Margin = new Padding(0, 0, 0, 10);
        _tituloQr.Text = "Aponte a câmera do celular para o QR Code";
        coluna.Controls.Add(_tituloQr);

        var moldura = _molduraQr;
        moldura.Dock = DockStyle.Fill;
        moldura.BackColor = Color.White;
        moldura.Padding = new Padding(10);
        moldura.Margin = new Padding(0, 0, 0, 12);
        _qr.Dock = DockStyle.Fill;
        _qr.SizeMode = PictureBoxSizeMode.Zoom;
        _qr.BackColor = Color.White;
        _qrAviso.Dock = DockStyle.Fill;
        _qrAviso.TextAlign = ContentAlignment.MiddleCenter;
        _qrAviso.ForeColor = Color.FromArgb(0x33, 0x33, 0x33);
        _qrAviso.Text = "Iniciando o servidor…";
        moldura.Controls.Add(_qr);
        moldura.Controls.Add(_qrAviso);
        coluna.Controls.Add(moldura);

        // O código de acesso: só quem está vendo esta tela consegue liberar a página no celular.
        var cartaoCodigo = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Fundo, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 0, 10) };
        cartaoCodigo.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cartaoCodigo.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tituloCodigo = new Label { Text = "Código de acesso — digite no celular ou leia o QR", AutoSize = true, ForeColor = Suave, Margin = new Padding(0) };
        cartaoCodigo.Controls.Add(tituloCodigo, 0, 0);
        cartaoCodigo.SetColumnSpan(tituloCodigo, 2);
        _codigo.AutoSize = true;
        // Monoespaçada: cada caractere com a mesma largura e forma inconfundível.
        _codigo.Font = new Font("Consolas", 23f, FontStyle.Bold);
        _codigo.ForeColor = Laranja;
        _codigo.Margin = new Padding(0, 2, 0, 0);
        _codigo.UseMnemonic = false;
        cartaoCodigo.Controls.Add(_codigo, 0, 1);
        _botaoQrCodigo = Botao("Mostrar como QR");
        _botaoQrCodigo.Anchor = AnchorStyles.Right;
        _botaoQrCodigo.Margin = new Padding(8, 4, 0, 0);
        _botaoQrCodigo.Click += (_, _) => AlternarQrDoCodigo(!_qrDoCodigo);
        cartaoCodigo.Controls.Add(_botaoQrCodigo, 1, 1);
        coluna.Controls.Add(cartaoCodigo);

        var linhaUrl = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        linhaUrl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        linhaUrl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _url.ReadOnly = true;
        _url.Dock = DockStyle.Fill;
        _url.BackColor = Fundo;
        _url.ForeColor = Suave;
        _url.BorderStyle = BorderStyle.FixedSingle;
        _url.Font = new Font("Consolas", 8.5f);
        _url.Margin = new Padding(0, 4, 6, 0);
        _url.AccessibleName = "Endereço da sessão";
        var copiar = Botao("Copiar link");
        copiar.Click += (_, _) => { if (_urlAtual is not null) Clipboard.SetText(_urlAtual); };
        linhaUrl.Controls.Add(_url, 0, 0);
        linhaUrl.Controls.Add(copiar, 1, 0);
        coluna.Controls.Add(linhaUrl);

        _conexao.AutoSize = true;
        _conexao.MaximumSize = new Size(360, 0);
        _conexao.Font = new Font("Segoe UI Semibold", 10f);
        _conexao.Margin = new Padding(0, 2, 0, 2);
        coluna.Controls.Add(_conexao);

        _expiracao.AutoSize = true;
        _expiracao.ForeColor = Suave;
        _expiracao.Margin = new Padding(0, 0, 0, 10);
        coluna.Controls.Add(_expiracao);

        var novo = Botao("Gerar novo QR Code", primario: true);
        novo.Dock = DockStyle.Top;
        novo.Click += (_, _) => GerarNovoQr();
        coluna.Controls.Add(novo);
        return coluna;
    }

    private Control MontarColunaTransferencias()
    {
        var coluna = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, BackColor = Fundo, Margin = new Padding(0) };
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coluna.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        coluna.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Pasta de destino
        var cartaoPasta = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, BackColor = Cartao, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 10) };
        cartaoPasta.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cartaoPasta.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        cartaoPasta.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tituloPasta = new Label { Text = "Pasta de destino", AutoSize = true, ForeColor = Suave, Margin = new Padding(0) };
        cartaoPasta.Controls.Add(tituloPasta, 0, 0);
        cartaoPasta.SetColumnSpan(tituloPasta, 3);
        _pasta.AutoEllipsis = true;
        _pasta.Dock = DockStyle.Fill;
        _pasta.TextAlign = ContentAlignment.MiddleLeft;
        _pasta.Font = new Font("Segoe UI Semibold", 10.5f);
        _pasta.ForeColor = Texto;
        _pasta.Height = 32;
        var trocar = Botao("Trocar pasta…");
        trocar.Click += (_, _) => TrocarPasta();
        var abrir = Botao("Abrir pasta");
        abrir.Click += (_, _) => AbrirNoExplorer(_envios.PastaDestino, selecionar: false);
        cartaoPasta.Controls.Add(_pasta, 0, 1);
        cartaoPasta.Controls.Add(trocar, 1, 1);
        cartaoPasta.Controls.Add(abrir, 2, 1);
        coluna.Controls.Add(cartaoPasta, 0, 0);

        // Rede
        var cartaoRede = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Cartao, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 10) };
        cartaoRede.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        cartaoRede.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tituloRede = new Label { Text = "Rede usada no QR Code (o celular precisa estar nesta mesma rede)", AutoSize = true, ForeColor = Suave, Margin = new Padding(0, 0, 0, 4) };
        cartaoRede.Controls.Add(tituloRede, 0, 0);
        cartaoRede.SetColumnSpan(tituloRede, 2);
        _rede.DropDownStyle = ComboBoxStyle.DropDownList;
        _rede.Dock = DockStyle.Fill;
        _rede.FlatStyle = FlatStyle.Flat;
        _rede.BackColor = Fundo;
        _rede.ForeColor = Texto;
        _rede.Margin = new Padding(0, 4, 8, 0);
        _rede.AccessibleName = "Rede usada no QR Code";
        _rede.SelectedIndexChanged += (_, _) =>
        {
            if (_rede.SelectedItem is EnderecoLocal e) { _config.UltimoIp = e.Ip.ToString(); _config.Salvar(); }
            AtualizarQr();
        };
        var atualizarRede = Botao("Atualizar");
        atualizarRede.Click += (_, _) => CarregarEnderecos();
        cartaoRede.Controls.Add(_rede, 0, 1);
        cartaoRede.Controls.Add(atualizarRede, 1, 1);
        coluna.Controls.Add(cartaoRede, 0, 1);

        _aviso.AutoSize = true;
        _aviso.MaximumSize = new Size(560, 0);
        _aviso.ForeColor = Amarelo;
        _aviso.Visible = false;
        _aviso.Margin = new Padding(0, 0, 0, 10);
        coluna.Controls.Add(_aviso, 0, 2);

        coluna.Controls.Add(new Label { Text = "Transferências", AutoSize = true, Font = new Font("Segoe UI Semibold", 11f), ForeColor = Texto, Margin = new Padding(0, 0, 0, 6) }, 0, 3);

        _lista.Dock = DockStyle.Fill;
        _lista.View = View.Details;
        _lista.FullRowSelect = true;
        _lista.MultiSelect = false;
        _lista.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _lista.BackColor = Cartao;
        _lista.ForeColor = Texto;
        _lista.BorderStyle = BorderStyle.None;
        _lista.OwnerDraw = true;
        _lista.AccessibleName = "Transferências";
        _lista.Columns.Add("Arquivo", 230);
        _lista.Columns.Add("Tamanho", 80, HorizontalAlignment.Right);
        _lista.Columns.Add("Progresso", 120);
        _lista.Columns.Add("Velocidade", 85, HorizontalAlignment.Right);
        _lista.Columns.Add("Situação", 140);
        _lista.DrawColumnHeader += DesenharCabecalho;
        _lista.DrawSubItem += DesenharCelula;
        _lista.DoubleClick += (_, _) =>
        {
            if (_lista.SelectedItems.Count == 1 && _lista.SelectedItems[0].Tag is Envio { CaminhoFinal: { } caminho })
                AbrirNoExplorer(caminho, selecionar: true);
        };
        _lista.Resize += (_, _) => AjustarColunas();
        coluna.Controls.Add(_lista, 0, 4);

        var acoes = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 0), FlowDirection = FlowDirection.LeftToRight };
        var limpar = Botao("Limpar terminados");
        limpar.Click += (_, _) => { _envios.LimparTerminados(); AtualizarLista(); };
        acoes.Controls.Add(limpar);
        acoes.Controls.Add(new Label { Text = "Duplo clique num arquivo concluído para mostrá-lo na pasta.", AutoSize = true, ForeColor = Suave, Margin = new Padding(8, 9, 0, 0) });
        coluna.Controls.Add(acoes, 0, 5);
        return coluna;
    }

    private static Button Botao(string texto, bool primario = false)
    {
        var b = new Button
        {
            Text = texto,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = primario ? Laranja : Color.FromArgb(0x26, 0x27, 0x2D),
            ForeColor = primario ? Color.FromArgb(0x1A, 0x0D, 0x02) : Texto,
            Font = new Font("Segoe UI Semibold", 9.75f),
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = primario ? Laranja : Borda;
        return b;
    }

    // ---------------------------------------------------------------- ciclo de vida

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        AjustarColunas();
        try
        {
            await _servidor.IniciarAsync();
            _servidorPronto = true;
            _rodape.Text = $"Versão {Versao()} · Servidor na porta {_servidor.Porta} · Smells Like Tech";
            AtualizarQr();
            _relogio.Start();
        }
        catch (Exception ex)
        {
            _qrAviso.Text = "Não foi possível iniciar o servidor.";
            MessageBox.Show(this, "Não foi possível iniciar o servidor local:\n\n" + ex.Message,
                Program.NomeApp, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (await Firewall.RegraExisteAsync() == false)
            MostrarAviso("A regra do Firewall do Windows não foi encontrada. Se o celular não conseguir abrir o link, reinstale o programa — o instalador cria a regra.");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var emAndamento = _envios.Lista().Count(x => !x.Terminado);
        if (!_fechando && emAndamento > 0 && e.CloseReason == CloseReason.UserClosing &&
            MessageBox.Show(this, $"Há {emAndamento} envio(s) em andamento. Fechar agora descarta o que ainda não terminou. Fechar mesmo assim?",
                Program.NomeApp, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        _fechando = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _relogio.Stop();
        NetworkChange.NetworkAddressChanged -= AoMudarRede;
        _sessoes.SessaoTrocada -= AoTrocarSessao;
        _esperaMostrar.Unregister(null);
        // Fora da thread da janela: o Kestrel termina as conexões sem precisar dela.
        Task.Run(async () => await _servidor.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
        _envios.Dispose();
        _sessoes.Dispose();
        _pedidoMostrar.Dispose();
        base.OnFormClosed(e);
    }

    private void TrazerParaFrente()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    // ---------------------------------------------------------------- ações

    private void GerarNovoQr()
    {
        var emAndamento = _envios.Lista().Count(x => !x.Terminado);
        if (emAndamento > 0 && MessageBox.Show(this,
                $"Há {emAndamento} envio(s) em andamento. Gerar um QR Code novo encerra a sessão atual e descarta o que não terminou. Continuar?",
                Program.NomeApp, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        _sessoes.Encerrar(MotivoEncerramento.EncerradaNoPc);
    }

    private void TrocarPasta()
    {
        var emAndamento = _envios.Lista().Count(x => !x.Terminado);
        if (emAndamento > 0 && MessageBox.Show(this,
                $"Há {emAndamento} envio(s) em andamento. Trocar a pasta encerra a sessão atual e descarta o que não terminou. Continuar?",
                Program.NomeApp, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        var nova = Program.EscolherPasta(_envios.PastaDestino, this);
        if (nova is null || string.Equals(Path.GetFullPath(nova), _envios.PastaDestino, StringComparison.OrdinalIgnoreCase)) return;

        // A sessão termina antes de trocar a pasta: nenhum envio começa numa e termina na outra.
        _sessoes.Encerrar(MotivoEncerramento.PastaAlterada);
        try { _envios.PastaDestino = nova; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, "Não foi possível usar esta pasta:\n\n" + ex.Message, Program.NomeApp, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _config.UltimaPasta = _envios.PastaDestino;
        _config.Salvar();
        AtualizarPasta();
    }

    private void AoTrocarSessao(Sessao antiga, MotivoEncerramento motivo, Sessao nova)
    {
        if (!IsHandleCreated || _fechando) return;
        BeginInvoke(() =>
        {
            _mensagemTroca = motivo switch
            {
                MotivoEncerramento.Inatividade => "A sessão anterior expirou por inatividade. QR Code novo gerado.",
                MotivoEncerramento.EncerradaNoCelular => "A sessão foi encerrada no celular. QR Code novo gerado.",
                MotivoEncerramento.PastaAlterada => "Pasta trocada. QR Code novo gerado.",
                MotivoEncerramento.TentativasEsgotadas => $"Código digitado errado {GerenciadorDeSessoes.MaxTentativas} vezes: sessão encerrada por segurança. QR Code e código novos gerados.",
                _ => "QR Code novo gerado. O anterior não funciona mais.",
            };
            _mensagemTrocaAte = DateTime.Now.AddSeconds(20);
            _qrDoCodigo = false;
            AtualizarQr();
        });
    }

    private void AoMudarRede(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !_fechando) BeginInvoke(CarregarEnderecos);
    }

    private void CarregarEnderecos()
    {
        var anterior = (_rede.SelectedItem as EnderecoLocal)?.Ip.ToString() ?? _config.UltimoIp;
        var enderecos = EnderecosLocais.Listar();
        _rede.BeginUpdate();
        _rede.Items.Clear();
        foreach (var e in enderecos) _rede.Items.Add(e);
        _rede.EndUpdate();

        var escolhido = enderecos.FirstOrDefault(e => e.Ip.ToString() == anterior) ?? enderecos.FirstOrDefault();
        if (escolhido is not null) _rede.SelectedItem = escolhido;
        AtualizarQr();

        if (enderecos.Count == 0)
            MostrarAviso("Nenhuma rede local encontrada. Conecte o PC ao Wi-Fi ou ao cabo de rede do mesmo roteador do celular.");
        else if (_aviso.Text.StartsWith("Nenhuma rede", StringComparison.Ordinal))
            _aviso.Visible = false;
    }

    private void MostrarAviso(string texto)
    {
        _aviso.Text = "⚠ " + texto;
        _aviso.Visible = true;
    }

    // ---------------------------------------------------------------- atualização da tela

    private void AtualizarPasta()
    {
        _pasta.Text = _envios.PastaDestino;
        _dica.SetToolTip(_pasta, _envios.PastaDestino);
    }

    private void AlternarQrDoCodigo(bool mostrar)
    {
        _qrDoCodigo = mostrar;
        AtualizarQr();
    }

    private void AtualizarQr()
    {
        if (!_servidorPronto) return;
        if (_rede.SelectedItem is not EnderecoLocal endereco)
        {
            _qr.Visible = false;
            _qrAviso.Visible = true;
            _qrAviso.Text = "Sem rede local.\nConecte o PC ao Wi-Fi ou ao cabo de rede.";
            _url.Text = "";
            _urlAtual = null;
            return;
        }

        var sessao = _sessoes.Atual;
        var url = _servidor.Endereco(endereco.Ip, sessao);
        _urlAtual = url;
        if (_url.Text != url) _url.Text = url;

        // O QR do código traz só o código (com prefixo), nunca o link: quem ler
        // apenas ele não chega à página.
        var conteudo = _qrDoCodigo ? GerenciadorDeSessoes.PrefixoQrCodigo + sessao.CodigoAcesso : url;
        _tituloQr.Text = _qrDoCodigo
            ? "QR do código de acesso: na página do celular, toque em \"Ler com a câmera\""
            : "Aponte a câmera do celular para o QR Code";
        _tituloQr.ForeColor = _qrDoCodigo ? Laranja : Texto;
        _molduraQr.BackColor = _qrDoCodigo ? Laranja : Color.White;
        _botaoQrCodigo.Text = _qrDoCodigo ? "Mostrar o link" : "Mostrar como QR";
        if (conteudo == _conteudoQr) return;
        _conteudoQr = conteudo;
        var antiga = _qr.Image;
        // Cópia em Bitmap: uma Image lida de stream exige o stream aberto enquanto existir.
        using (var png = new MemoryStream(GeradorDeQr.Png(conteudo, robusto: _qrDoCodigo)))
        using (var lida = Image.FromStream(png))
            _qr.Image = new Bitmap(lida);
        antiga?.Dispose();
        _qr.Visible = true;
        _qrAviso.Visible = false;
        Atualizar();
    }

    private void Atualizar()
    {
        var sessao = _sessoes.Atual;
        if (_urlAtual is not null && !_urlAtual.EndsWith(sessao.Token, StringComparison.Ordinal)) AtualizarQr();
        // Depois que o celular entrou, o QR do código não tem mais serventia.
        if (_qrDoCodigo && sessao.Autenticada) AlternarQrDoCodigo(false);

        var codigo = $"{sessao.CodigoAcesso[..4]} {sessao.CodigoAcesso[4..]}";
        if (_codigo.Text != codigo)
        {
            _codigo.Text = codigo;
            _codigo.AccessibleName = "Código de acesso " + codigo;
        }

        if (sessao.Aparelho is { } aparelho)
        {
            _conexao.Text = $"● Celular conectado ({aparelho})";
            _conexao.ForeColor = Verde;
        }
        else if (sessao.TentativasErradas > 0)
        {
            var restam = GerenciadorDeSessoes.MaxTentativas - sessao.TentativasErradas;
            _conexao.Text = $"● Código incorreto digitado no celular ({restam} {(restam == 1 ? "tentativa restante" : "tentativas restantes")})";
            _conexao.ForeColor = Amarelo;
        }
        else if (_mensagemTroca is not null && DateTime.Now < _mensagemTrocaAte && !sessao.PaginaAberta)
        {
            _conexao.Text = _mensagemTroca;
            _conexao.ForeColor = Laranja;
        }
        else if (sessao.PaginaAberta)
        {
            _conexao.Text = "● Link aberto no celular — aguardando o código de acesso";
            _conexao.ForeColor = Amarelo;
        }
        else
        {
            _conexao.Text = "● Aguardando o celular";
            _conexao.ForeColor = Suave;
        }

        var restante = _sessoes.TempoAteExpirar(sessao);
        _expiracao.Text = $"Sem atividade, este QR Code expira em {(int)restante.TotalMinutes}:{restante.Seconds:00}";
        AtualizarLista();
    }

    private void AtualizarLista()
    {
        var envios = _envios.Lista();
        var porId = _lista.Items.Cast<ListViewItem>().ToDictionary(i => ((Envio)i.Tag!).Id);
        var agora = DateTimeOffset.UtcNow;

        _lista.BeginUpdate();
        foreach (var item in porId.Values.Where(i => !envios.Contains((Envio)i.Tag!)).ToList())
            _lista.Items.Remove(item);

        foreach (var envio in envios)
        {
            if (!porId.TryGetValue(envio.Id, out var item))
            {
                item = new ListViewItem([envio.NomeSeguro, Formatos.Bytes(envio.Tamanho), "", "", ""]) { Tag = envio, ForeColor = Texto, BackColor = Cartao };
                _lista.Items.Add(item);
            }

            var parado = envio.Situacao == SituacaoEnvio.Recebendo && agora - envio.UltimaGravacao > TimeSpan.FromSeconds(3);
            var nome = envio.CaminhoFinal is { } final ? Path.GetFileName(final) : envio.NomeSeguro;
            var velocidade = envio.Situacao == SituacaoEnvio.Recebendo && !parado && envio.Velocidade > 0 ? Formatos.Bytes((long)envio.Velocidade) + "/s" : "";
            var situacao = envio.Situacao switch
            {
                SituacaoEnvio.Recebendo when parado => "Aguardando o celular",
                SituacaoEnvio.Recebendo => "Recebendo",
                SituacaoEnvio.Pausado => "Pausado no celular",
                SituacaoEnvio.Concluido => "Concluído",
                SituacaoEnvio.Cancelado => envio.Erro ?? "Cancelado no celular",
                _ => envio.Erro ?? "Falhou",
            };
            Definir(item, 0, nome);
            Definir(item, 3, velocidade);
            Definir(item, 4, situacao);
            Definir(item, 2, Porcentagem(envio).ToString(CultureInfo.InvariantCulture));
        }
        _lista.EndUpdate();
    }

    private static void Definir(ListViewItem item, int coluna, string texto)
    {
        if (item.SubItems[coluna].Text != texto) item.SubItems[coluna].Text = texto;
    }

    private static int Porcentagem(Envio e) =>
        e.Situacao == SituacaoEnvio.Concluido ? 100 : e.Tamanho == 0 ? 0 : (int)(e.Recebido * 100 / e.Tamanho);

    private void AjustarColunas()
    {
        var fixas = _lista.Columns[1].Width + _lista.Columns[2].Width + _lista.Columns[3].Width + _lista.Columns[4].Width;
        _lista.Columns[0].Width = Math.Max(120, _lista.ClientSize.Width - fixas - 4);
    }

    private void DesenharCabecalho(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var fundo = new SolidBrush(Fundo);
        e.Graphics.FillRectangle(fundo, e.Bounds);
        var alinhamento = e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        var area = Rectangle.Inflate(e.Bounds, -6, 0);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text, Font, area, Suave, alinhamento | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DesenharCelula(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null) return;
        var envio = (Envio)e.Item.Tag!;
        var fundo = e.Item.Selected ? Borda : Cartao;
        using (var pincel = new SolidBrush(fundo)) e.Graphics.FillRectangle(pincel, e.Bounds);

        if (e.ColumnIndex == 2)
        {
            var pct = Porcentagem(envio);
            var barra = new Rectangle(e.Bounds.X + 6, e.Bounds.Y + e.Bounds.Height / 2 - 4, Math.Max(0, e.Bounds.Width - 52), 8);
            var cor = envio.Situacao switch
            {
                SituacaoEnvio.Concluido => Verde,
                SituacaoEnvio.Pausado => Amarelo,
                SituacaoEnvio.Cancelado or SituacaoEnvio.Falhou => Vermelho,
                _ => Laranja,
            };
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var trilho = new SolidBrush(Color.FromArgb(0x2A, 0x2B, 0x31))) e.Graphics.FillRectangle(trilho, barra);
            using (var preenchido = new SolidBrush(cor))
                e.Graphics.FillRectangle(preenchido, barra with { Width = barra.Width * pct / 100 });
            var areaTexto = new Rectangle(barra.Right + 4, e.Bounds.Y, e.Bounds.Right - barra.Right - 4, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, pct + "%", Font, areaTexto, Texto, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            return;
        }

        var cor2 = e.ColumnIndex == 4 ? envio.Situacao switch
        {
            SituacaoEnvio.Concluido => Verde,
            SituacaoEnvio.Pausado => Amarelo,
            SituacaoEnvio.Cancelado or SituacaoEnvio.Falhou => Vermelho,
            _ => Texto,
        } : Texto;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    (_lista.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, Rectangle.Inflate(e.Bounds, -6, 0), cor2, flags);
    }

    // ---------------------------------------------------------------- utilidades

    private static void AbrirNoExplorer(string caminho, bool selecionar)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = selecionar ? $"/select,\"{caminho}\"" : $"\"{caminho}\"",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static Icon? CarregarIcone()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SltDropLocal.icon.ico");
        return s is null ? null : new Icon(s);
    }

    private static string Versao() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Application.ProductVersion;
}

internal static class Formatos
{
    public static string Bytes(long n)
    {
        if (n < 1024) return n + " B";
        string[] unidades = ["KB", "MB", "GB", "TB"];
        double v = n / 1024d;
        var i = 0;
        while (v >= 1024 && i < unidades.Length - 1) { v /= 1024; i++; }
        return v.ToString(v < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("pt-BR")) + " " + unidades[i];
    }
}
