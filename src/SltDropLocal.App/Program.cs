using System.Globalization;

namespace SltDropLocal.App;

internal static class Program
{
    public const string NomeApp = "SLT Drop Local QRCode";
    private const string NomeMutex = @"Local\SLTDropLocalQRCode";
    internal const string NomeEventoMostrar = @"Local\SLTDropLocalQRCode.Mostrar";

    /// <summary>
    /// Opções de linha de comando (todas opcionais):
    ///   --pasta "D:\Recebidos"        usa a pasta sem abrir a janela de escolha
    ///   --porta 8770                   porta preferida do servidor
    ///   --inatividade-segundos 900     tempo sem atividade até a sessão expirar
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("pt-BR");

        using var mutex = new Mutex(initiallyOwned: true, NomeMutex, out var primeiraInstancia);
        if (!primeiraInstancia)
        {
            // Já está aberto: pede para a janela existente vir para a frente.
            try { using var evento = EventWaitHandle.OpenExisting(NomeEventoMostrar); evento.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        ApplicationConfiguration.Initialize();

        Opcoes opcoes;
        try { opcoes = Opcoes.Ler(args); }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, NomeApp, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        var config = Configuracoes.Carregar();

        // A pasta de destino é escolhida logo ao abrir, antes de qualquer QR Code existir.
        var pasta = opcoes.Pasta ?? EscolherPasta(config.UltimaPasta);
        if (pasta is null) return 0;
        config.UltimaPasta = pasta;
        config.Salvar();

        Application.Run(new JanelaPrincipal(pasta, opcoes, config));
        return 0;
    }

    internal static string? EscolherPasta(string? inicial, IWin32Window? dono = null)
    {
        using var dialogo = new FolderBrowserDialog
        {
            Description = "Escolha a pasta onde os arquivos enviados pelo celular serão salvos",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(inicial) ? inicial! : PastaDownloads(),
        };
        return dialogo.ShowDialog(dono) == DialogResult.OK ? dialogo.SelectedPath : null;
    }

    private static string PastaDownloads()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }
}

internal sealed record Opcoes(string? Pasta, int? Porta, TimeSpan? Inatividade)
{
    public static Opcoes Ler(string[] args)
    {
        string? pasta = null;
        int? porta = null;
        TimeSpan? inatividade = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Valor() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Falta o valor de {args[i]}.");
            switch (args[i].ToLowerInvariant())
            {
                case "--pasta":
                    pasta = Path.GetFullPath(Valor());
                    if (!Directory.Exists(pasta)) throw new ArgumentException($"A pasta não existe: {pasta}");
                    break;
                case "--porta":
                    porta = int.TryParse(Valor(), out var p) && p is > 0 and < 65536 ? p : throw new ArgumentException("Porta inválida.");
                    break;
                case "--inatividade-segundos":
                    inatividade = int.TryParse(Valor(), out var s) && s > 0 ? TimeSpan.FromSeconds(s) : throw new ArgumentException("Tempo de inatividade inválido.");
                    break;
                default:
                    throw new ArgumentException($"Opção desconhecida: {args[i]}");
            }
        }
        return new Opcoes(pasta, porta, inatividade);
    }
}
