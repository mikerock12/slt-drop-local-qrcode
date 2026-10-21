using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SltDropLocal.Core.Sessoes;

/// <summary>Resultado de apresentar token (e chave de acesso) ao servidor.</summary>
public enum Acesso
{
    Permitido,
    /// <summary>O token pertenceu a uma sessão que já terminou.</summary>
    Expirada,
    /// <summary>O token nunca existiu (ou é de uma execução anterior do programa).</summary>
    Invalida,
    /// <summary>A sessão é válida, mas já pertence a outro aparelho.</summary>
    OutroAparelho,
    /// <summary>Token certo, mas o celular ainda não digitou o código de acesso.</summary>
    CodigoNecessario,
}

public enum SituacaoCodigo
{
    Aceito,
    Incorreto,
    /// <summary>Errou o código vezes demais: a sessão foi encerrada.</summary>
    Esgotado,
    Expirada,
    Invalida,
    OutroAparelho,
}

public sealed record ResultadoCodigo(SituacaoCodigo Situacao, string? Chave = null, int Restantes = 0);

/// <summary>
/// Mantém a única sessão válida do momento. Há sempre exatamente uma: quando ela
/// termina (inatividade, botão no celular, botão no PC, código errado demais),
/// outra nasce na mesma hora com token e código de acesso novos, e o QR Code muda junto.
///
/// Para enviar arquivos o celular precisa das duas coisas: o token (vem no QR
/// Code) e o código de acesso (só aparece escrito na tela do PC). Acertando o
/// código, recebe uma chave aleatória que acompanha cada requisição em cookie.
/// </summary>
public sealed class GerenciadorDeSessoes : IDisposable
{
    public static readonly TimeSpan InatividadePadrao = TimeSpan.FromMinutes(15);
    public const int MaxTentativas = 5;
    public const int TamanhoCodigo = 8;

    /// <summary>O QR Code do código de acesso traz este prefixo, para a página reconhecer que é dela.</summary>
    public const string PrefixoQrCodigo = "SLTDROP:";

    // Sem os caracteres que se confundem na tela ou no teclado: I/l/1, O/o/0.
    internal const string Maiusculas = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    internal const string Minusculas = "abcdefghijkmnpqrstuvwxyz";
    internal const string Digitos = "23456789";
    internal const string Simbolos = "!@#$%&*?+=";

    // Só para dizer "sessão expirada" em vez de "link inválido" a quem volta a uma
    // aba antiga. Guardamos o hash, não o token; o limite evita crescer sem fim.
    private const int MaxExpiradasLembradas = 2000;

    private readonly TimeProvider _relogio;
    private readonly HistoricoDeCodigos _historico;
    private readonly ITimer _verificador;
    private readonly Lock _trava = new();
    private readonly HashSet<string> _expiradas = [];
    private readonly Queue<string> _ordemExpiradas = new();
    private Sessao _atual;
    private bool _descartado;

    public GerenciadorDeSessoes(TimeProvider? relogio = null, TimeSpan? limiteInatividade = null,
        HistoricoDeCodigos? historico = null)
    {
        _relogio = relogio ?? TimeProvider.System;
        _historico = historico ?? new HistoricoDeCodigos();
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
    /// Abrir a página só exige o token: é ela que pede o código. Uma sessão que
    /// já tem dono continua fechada para os outros aparelhos.
    /// </summary>
    public Acesso AbrirPagina(string token, IPAddress? cliente, out Sessao? sessao)
    {
        lock (_trava)
        {
            var acesso = ConferirToken(token, cliente, out sessao);
            if (acesso == Acesso.Permitido) sessao!.PaginaAberta = true;
            return acesso;
        }
    }

    /// <summary>Para a API: exige token, código já validado (chave) e o mesmo aparelho.</summary>
    public Acesso Autorizar(string token, IPAddress? cliente, string? chave, out Sessao? sessao)
    {
        lock (_trava)
        {
            var acesso = ConferirToken(token, cliente, out sessao);
            if (acesso != Acesso.Permitido) return acesso;
            if (!ChaveConfere(sessao!, chave))
            {
                sessao = null;
                return Acesso.CodigoNecessario;
            }
            return Acesso.Permitido;
        }
    }

    /// <summary>Confere o código digitado no celular. Errar <see cref="MaxTentativas"/> vezes encerra a sessão.</summary>
    public ResultadoCodigo VerificarCodigo(string token, IPAddress? cliente, string? codigo)
    {
        Sessao sessao;
        lock (_trava)
        {
            switch (ConferirToken(token, cliente, out var s))
            {
                case Acesso.Expirada: return new(SituacaoCodigo.Expirada);
                case Acesso.Invalida: return new(SituacaoCodigo.Invalida);
                case Acesso.OutroAparelho: return new(SituacaoCodigo.OutroAparelho);
            }
            sessao = s!;

            // Diferencia maiúsculas de minúsculas; só os espaços (da exibição em dois grupos) são ignorados.
            var digitado = string.Concat((codigo ?? "").Where(c => !char.IsWhiteSpace(c)).Take(64));
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(digitado), Encoding.UTF8.GetBytes(sessao.CodigoAcesso)))
            {
                var chave = GerarToken();
                sessao.Aparelho = Normalizar(cliente);
                sessao.ChaveHash = SHA256.HashData(Encoding.UTF8.GetBytes(chave));
                sessao.RegistrarAtividade(_relogio.GetUtcNow());
                return new(SituacaoCodigo.Aceito, chave);
            }

            sessao.TentativasErradas++;
            if (sessao.TentativasErradas < MaxTentativas)
                return new(SituacaoCodigo.Incorreto, Restantes: MaxTentativas - sessao.TentativasErradas);
        }

        Encerrar(MotivoEncerramento.TentativasEsgotadas, somenteSe: sessao);
        return new(SituacaoCodigo.Esgotado);
    }

    /// <summary>Qualquer ação do celular (abrir a página, enviar bytes, cancelar) adia a expiração.</summary>
    public void RegistrarAtividade(Sessao sessao) => sessao.RegistrarAtividade(_relogio.GetUtcNow());

    public TimeSpan TempoAteExpirar(Sessao sessao)
    {
        var restante = sessao.UltimaAtividade + LimiteInatividade - _relogio.GetUtcNow();
        return restante < TimeSpan.Zero ? TimeSpan.Zero : restante;
    }

    /// <summary>Encerra a sessão atual (se ainda for a atual) e cria outra com token e código novos.</summary>
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

    private Acesso ConferirToken(string token, IPAddress? cliente, out Sessao? sessao)
    {
        sessao = null;
        if (!TokensIguais(token, _atual.Token))
            return _expiradas.Contains(Hash(token)) ? Acesso.Expirada : Acesso.Invalida;
        if (_atual.Aparelho is not null && !_atual.Aparelho.Equals(Normalizar(cliente)))
            return Acesso.OutroAparelho;
        sessao = _atual;
        return Acesso.Permitido;
    }

    private static bool ChaveConfere(Sessao sessao, string? chave) =>
        sessao.ChaveHash is { } esperado && !string.IsNullOrEmpty(chave) &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(chave)), esperado);

    private Sessao NovaSessao() => new(GerarToken(), GerarCodigo(), _relogio.GetUtcNow());

    /// <summary>
    /// 8 caracteres com pelo menos uma maiúscula, uma minúscula, um número e um
    /// símbolo (cerca de 47 bits), que nunca repetem um código do histórico.
    /// </summary>
    private string GerarCodigo()
    {
        while (true)
        {
            var codigo = NovoCodigo();
            if (_historico.Reservar(codigo)) return codigo;
        }
    }

    internal static string NovoCodigo()
    {
        const string todos = Maiusculas + Minusculas + Digitos + Simbolos;
        var c = new char[TamanhoCodigo];
        c[0] = Sortear(Maiusculas);
        c[1] = Sortear(Minusculas);
        c[2] = Sortear(Digitos);
        c[3] = Sortear(Simbolos);
        for (var i = 4; i < c.Length; i++) c[i] = Sortear(todos);
        // Embaralha (Fisher-Yates) para a posição de cada tipo não ser previsível.
        for (var i = c.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (c[i], c[j]) = (c[j], c[i]);
        }
        return new string(c);

        static char Sortear(string de) => de[RandomNumberGenerator.GetInt32(de.Length)];
    }

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
