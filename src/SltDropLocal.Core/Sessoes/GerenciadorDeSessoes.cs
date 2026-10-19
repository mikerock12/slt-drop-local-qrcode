using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SltDropLocal.Core.Sessoes;

/// <summary>Resultado de apresentar um token ao servidor.</summary>
public enum Acesso
{
    Permitido,
    /// <summary>O token pertenceu a uma sessão que já terminou.</summary>
    Expirada,
    /// <summary>O token nunca existiu (ou é de uma execução anterior do programa).</summary>
    Invalida,
    /// <summary>A sessão é válida, mas já pertence a outro aparelho.</summary>
    OutroAparelho,
}

/// <summary>
/// Mantém a única sessão válida do momento. Há sempre exatamente uma: quando ela
/// termina (inatividade, botão no celular, botão no PC), outra nasce na mesma hora
/// com token novo, e o QR Code muda junto.
/// </summary>
public sealed class GerenciadorDeSessoes : IDisposable
{
    public static readonly TimeSpan InatividadePadrao = TimeSpan.FromMinutes(15);

    // Só para dizer "sessão expirada" em vez de "link inválido" a quem volta a uma
    // aba antiga. Guardamos o hash, não o token; o limite evita crescer sem fim.
    private const int MaxExpiradasLembradas = 2000;

    private readonly TimeProvider _relogio;
    private readonly ITimer _verificador;
    private readonly Lock _trava = new();
    private readonly HashSet<string> _expiradas = [];
    private readonly Queue<string> _ordemExpiradas = new();
    private Sessao _atual;
    private bool _descartado;

    public GerenciadorDeSessoes(TimeProvider? relogio = null, TimeSpan? limiteInatividade = null)
    {
        _relogio = relogio ?? TimeProvider.System;
        LimiteInatividade = limiteInatividade ?? InatividadePadrao;
        if (LimiteInatividade <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limiteInatividade));

        _atual = NovaSessao();
        _verificador = _relogio.CreateTimer(_ => VerificarInatividade(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public TimeSpan LimiteInatividade { get; }

    public Sessao Atual
    {
        get { lock (_trava) return _atual; }
    }

    /// <summary>Disparado depois que a sessão antiga terminou e a nova já está valendo.</summary>
    public event Action<Sessao, MotivoEncerramento, Sessao>? SessaoTrocada;

    /// <summary>
    /// Confere o token. <paramref name="reivindicar"/> liga a sessão ao aparelho na
    /// primeira vez (abrir a página, começar um envio); consultas não reivindicam.
    /// </summary>
    public Acesso Autorizar(string token, IPAddress? cliente, bool reivindicar, out Sessao? sessao)
    {
        sessao = null;
        lock (_trava)
        {
            if (!TokensIguais(token, _atual.Token))
                return _expiradas.Contains(Hash(token)) ? Acesso.Expirada : Acesso.Invalida;

            var ip = Normalizar(cliente);
            if (_atual.Aparelho is null)
            {
                if (reivindicar && ip is not null) _atual.Aparelho = ip;
            }
            else if (ip is null || !_atual.Aparelho.Equals(ip))
            {
                return Acesso.OutroAparelho;
            }

            sessao = _atual;
            return Acesso.Permitido;
        }
    }

    /// <summary>Qualquer ação do celular (abrir a página, enviar bytes, cancelar) adia a expiração.</summary>
    public void RegistrarAtividade(Sessao sessao) => sessao.RegistrarAtividade(_relogio.GetUtcNow());

    public TimeSpan TempoAteExpirar(Sessao sessao)
    {
        var restante = sessao.UltimaAtividade + LimiteInatividade - _relogio.GetUtcNow();
        return restante < TimeSpan.Zero ? TimeSpan.Zero : restante;
    }

    /// <summary>Encerra a sessão atual (se ainda for a atual) e cria outra com token novo.</summary>
    public Sessao Encerrar(MotivoEncerramento motivo, Sessao? somenteSe = null)
    {
        Sessao antiga, nova;
        lock (_trava)
        {
            if (_descartado) return _atual;
            if (somenteSe is not null && !ReferenceEquals(somenteSe, _atual)) return _atual;

            antiga = _atual;
            antiga.Encerrar(motivo);
            LembrarExpirada(antiga.Token);
            nova = _atual = NovaSessao();
        }

        SessaoTrocada?.Invoke(antiga, motivo, nova);
        return nova;
    }

    /// <summary>Chamado pelo cronômetro a cada segundo; público para os testes não dependerem dele.</summary>
    public void VerificarInatividade()
    {
        var sessao = Atual;
        if (sessao.Ativa && TempoAteExpirar(sessao) == TimeSpan.Zero)
            Encerrar(MotivoEncerramento.Inatividade, somenteSe: sessao);
    }

    public void Dispose()
    {
        _verificador.Dispose();
        Sessao atual;
        lock (_trava)
        {
            if (_descartado) return;
            _descartado = true;
            atual = _atual;
        }
        atual.Encerrar(MotivoEncerramento.AplicativoFechado);
    }

    private Sessao NovaSessao() => new(GerarToken(), _relogio.GetUtcNow());

    private void LembrarExpirada(string token)
    {
        var hash = Hash(token);
        if (!_expiradas.Add(hash)) return;
        _ordemExpiradas.Enqueue(hash);
        while (_ordemExpiradas.Count > MaxExpiradasLembradas)
            _expiradas.Remove(_ordemExpiradas.Dequeue());
    }

    /// <summary>256 bits aleatórios em base64url: impossível de adivinhar, curto o bastante para o QR Code.</summary>
    internal static string GerarToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TokensIguais(string recebido, string esperado) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(recebido ?? ""), Encoding.UTF8.GetBytes(esperado));

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    private static IPAddress? Normalizar(IPAddress? ip) =>
        ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;
}
