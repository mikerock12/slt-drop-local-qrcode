using System.Net;

namespace SltDropLocal.Core.Sessoes;

/// <summary>Por que uma sessão terminou. Toda sessão termina por exatamente um motivo.</summary>
public enum MotivoEncerramento
{
    Inatividade,
    EncerradaNoCelular,
    EncerradaNoPc,
    PastaAlterada,
    /// <summary>Código de acesso errado vezes demais.</summary>
    TentativasEsgotadas,
    AplicativoFechado,
}

/// <summary>
/// Uma sessão de envio. Tem duas chaves: o token que vai no QR Code (abre a
/// página) e o código de acesso que só aparece na tela do PC (libera a página).
/// Encerrada uma vez, nunca volta a valer — o gerenciador cria outra com token e
/// código novos.
/// </summary>
public sealed class Sessao
{
    private readonly CancellationTokenSource _encerramento = new();
    private long _ultimaAtividadeTicks;

    internal Sessao(string token, string codigoAcesso, DateTimeOffset criadaEm)
    {
        Token = token;
        CodigoAcesso = codigoAcesso;
        CriadaEm = criadaEm;
        _ultimaAtividadeTicks = criadaEm.UtcTicks;
    }

    public string Token { get; }

    /// <summary>8 caracteres mostrados só no PC. O celular precisa digitá-los (ou ler o QR deles) para entrar.</summary>
    public string CodigoAcesso { get; }

    public DateTimeOffset CriadaEm { get; }

    /// <summary>O aparelho dono da sessão: o que digitou o código certo primeiro.</summary>
    public IPAddress? Aparelho { get; internal set; }

    /// <summary>Alguém abriu o link do QR Code (ainda sem o código, talvez).</summary>
    public bool PaginaAberta { get; internal set; }

    public int TentativasErradas { get; internal set; }

    public bool Autenticada => ChaveHash is not null;

    /// <summary>SHA-256 da chave entregue ao celular (em cookie) depois do código certo.</summary>
    internal byte[]? ChaveHash { get; set; }

    public DateTimeOffset UltimaAtividade =>
        new(Interlocked.Read(ref _ultimaAtividadeTicks), TimeSpan.Zero);

    public bool Ativa => !_encerramento.IsCancellationRequested;
    public MotivoEncerramento? Motivo { get; private set; }

    /// <summary>Cancelado quando a sessão termina: interrompe envios que ainda estejam gravando.</summary>
    public CancellationToken Encerrando => _encerramento.Token;

    internal void RegistrarAtividade(DateTimeOffset agora) =>
        Interlocked.Exchange(ref _ultimaAtividadeTicks, agora.UtcTicks);

    internal void Encerrar(MotivoEncerramento motivo)
    {
        Motivo = motivo;
        _encerramento.Cancel();
    }
}
