using System.Net;

namespace SltDropLocal.Core.Sessoes;

/// <summary>Por que uma sessão terminou. Toda sessão termina por exatamente um motivo.</summary>
public enum MotivoEncerramento
{
    Inatividade,
    EncerradaNoCelular,
    EncerradaNoPc,
    PastaAlterada,
    AplicativoFechado,
}

/// <summary>
/// Uma sessão de envio: o token que vai no QR Code e o aparelho que o usou primeiro.
/// Encerrada uma vez, nunca volta a valer — o gerenciador cria outra com token novo.
/// </summary>
public sealed class Sessao
{
    private readonly CancellationTokenSource _encerramento = new();
    private long _ultimaAtividadeTicks;

    internal Sessao(string token, DateTimeOffset criadaEm)
    {
        Token = token;
        CriadaEm = criadaEm;
        _ultimaAtividadeTicks = criadaEm.UtcTicks;
    }

    public string Token { get; }
    public DateTimeOffset CriadaEm { get; }

    /// <summary>O aparelho dono da sessão: o primeiro IP que abriu a página ou começou um envio.</summary>
    public IPAddress? Aparelho { get; internal set; }

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
