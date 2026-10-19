namespace SltDropLocal.Core.Envios;

public enum SituacaoEnvio
{
    Recebendo,
    Pausado,
    Concluido,
    Cancelado,
    Falhou,
}

/// <summary>Um arquivo vindo do celular. Os bytes vão para um .part e só viram o arquivo final no fim.</summary>
public sealed class Envio
{
    private long _recebido;

    internal Envio(string id, string sessaoToken, string nomeOriginal, string nomeSeguro,
        long tamanho, string caminhoParcial, DateTimeOffset inicio)
    {
        Id = id;
        SessaoToken = sessaoToken;
        NomeOriginal = nomeOriginal;
        NomeSeguro = nomeSeguro;
        Tamanho = tamanho;
        CaminhoParcial = caminhoParcial;
        Inicio = inicio;
        UltimaGravacao = inicio;
    }

    public string Id { get; }
    internal string SessaoToken { get; }
    public string NomeOriginal { get; }
    public string NomeSeguro { get; }
    public long Tamanho { get; }
    internal string CaminhoParcial { get; }
    public DateTimeOffset Inicio { get; }

    public long Recebido
    {
        get => Interlocked.Read(ref _recebido);
        internal set => Interlocked.Exchange(ref _recebido, value);
    }

    public SituacaoEnvio Situacao { get; internal set; } = SituacaoEnvio.Recebendo;
    public string? CaminhoFinal { get; internal set; }
    public string? Erro { get; internal set; }
    public DateTimeOffset UltimaGravacao { get; internal set; }

    /// <summary>Bytes por segundo nos últimos segundos, para a lista do PC.</summary>
    public double Velocidade { get; internal set; }

    public bool Terminado => Situacao is SituacaoEnvio.Concluido or SituacaoEnvio.Cancelado or SituacaoEnvio.Falhou;

    /// <summary>Só uma requisição grava por vez; a segunda recebe 409 e consulta de onde continuar.</summary>
    internal SemaphoreSlim Gravacao { get; } = new(1, 1);

    /// <summary>Cancela a gravação em andamento (cancelamento do envio ou fim da sessão).</summary>
    internal CancellationTokenSource Cancelamento { get; } = new();

    // Janela para a velocidade.
    internal long AmostraBytes;
    internal DateTimeOffset AmostraInicio;
}
