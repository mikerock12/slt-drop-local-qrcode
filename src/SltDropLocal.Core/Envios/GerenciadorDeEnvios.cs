using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using SltDropLocal.Core.Sessoes;

namespace SltDropLocal.Core.Envios;

public enum ResultadoGravacao
{
    /// <summary>Bloco gravado; o arquivo ainda não está completo.</summary>
    Gravado,
    Concluido,
    /// <summary>O bloco não começa onde o PC parou; o cliente deve continuar de <c>Recebido</c>.</summary>
    PosicaoErrada,
    /// <summary>Outra requisição ainda está gravando este envio (ex.: a anterior a uma pausa).</summary>
    Ocupado,
    /// <summary>O envio foi cancelado ou a sessão terminou.</summary>
    Cancelado,
    /// <summary>Vieram mais bytes do que o tamanho declarado do arquivo.</summary>
    ConteudoDemais,
    /// <summary>O celular parou de mandar no meio do bloco (pausa, rede caiu). O que chegou fica salvo.</summary>
    Interrompido,
    /// <summary>Erro do disco do PC (cheio, sem permissão...).</summary>
    FalhaNoDisco,
}

public sealed record RespostaGravacao(ResultadoGravacao Resultado, long Recebido, string? NomeFinal = null, string? Erro = null);

public sealed class EspacoInsuficienteException(long livre, long necessario)
    : IOException($"Espaço insuficiente no PC: livre {livre} bytes, necessário {necessario} bytes.")
{
    public long Livre { get; } = livre;
    public long Necessario { get; } = necessario;
}

/// <summary>
/// Recebe arquivos de qualquer tamanho em blocos. Cada bloco é escrito direto no
/// disco (nada inteiro na memória) num .part dentro da própria pasta de destino;
/// no último byte o .part é renomeado para o nome final, o que é instantâneo
/// mesmo para arquivos de dezenas de gigabytes porque não há cópia.
/// </summary>
public sealed class GerenciadorDeEnvios : IDisposable
{
    public const string PastaParciais = ".sltdrop-parciais";
    private const int TamanhoBuffer = 1024 * 1024;

    private readonly TimeProvider _relogio;
    private readonly ConcurrentDictionary<string, Envio> _envios = new();
    private readonly ConcurrentDictionary<string, string> _pastaDoEnvio = new();
    private readonly Lock _travaNomes = new();
    private string _pastaDestino;

    public GerenciadorDeEnvios(string pastaDestino, TimeProvider? relogio = null)
    {
        _relogio = relogio ?? TimeProvider.System;
        _pastaDestino = PrepararPasta(pastaDestino);
    }

    public string PastaDestino
    {
        get => Volatile.Read(ref _pastaDestino);
        set => Volatile.Write(ref _pastaDestino, PrepararPasta(value));
    }

    public event Action<Envio>? EnvioConcluido;

    public IReadOnlyList<Envio> Lista() => [.. _envios.Values.OrderBy(e => e.Inicio).ThenBy(e => e.Id)];

    public Envio Criar(Sessao sessao, string? nome, long tamanho)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tamanho);
        if (!sessao.Ativa) throw new InvalidOperationException("Sessão encerrada.");

        var pasta = PastaDestino;
        var livre = EspacoLivre(pasta);
        if (livre is long l && l < tamanho) throw new EspacoInsuficienteException(l, tamanho);

        var parciais = Path.Combine(pasta, PastaParciais);
        var dir = Directory.CreateDirectory(parciais);
        dir.Attributes |= FileAttributes.Hidden;

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var parcial = Path.Combine(parciais, id + ".part");
        File.Create(parcial).Dispose();

        var original = string.IsNullOrWhiteSpace(nome) ? "arquivo" : nome.Trim();
        var envio = new Envio(id, sessao.Token, original, NomesDeArquivo.Higienizar(original),
            tamanho, parcial, _relogio.GetUtcNow());
        envio.AmostraInicio = envio.Inicio;
        _pastaDoEnvio[id] = pasta;
        _envios[id] = envio;

        if (tamanho == 0) Finalizar(envio);
        return envio;
    }

    /// <summary>Só devolve o envio para a sessão que o criou.</summary>
    public Envio? Obter(Sessao sessao, string id) =>
        _envios.TryGetValue(id ?? "", out var e) && e.SessaoToken == sessao.Token ? e : null;

    public async Task<RespostaGravacao> GravarAsync(Sessao sessao, Envio envio, long posicao, Stream corpo,
        Action? aoGravar = null, CancellationToken requisicaoAbortada = default)
    {
        if (envio.Situacao == SituacaoEnvio.Concluido)
            return new(ResultadoGravacao.Concluido, envio.Recebido, Path.GetFileName(envio.CaminhoFinal));
        if (envio.Terminado || !sessao.Ativa)
            return new(ResultadoGravacao.Cancelado, envio.Recebido);

        if (!await envio.Gravacao.WaitAsync(0))
            return new(ResultadoGravacao.Ocupado, envio.Recebido);

        try
        {
            if (posicao != envio.Recebido)
                return new(ResultadoGravacao.PosicaoErrada, envio.Recebido);

            using var cancelar = CancellationTokenSource.CreateLinkedTokenSource(
                requisicaoAbortada, envio.Cancelamento.Token, sessao.Encerrando);
            envio.Situacao = SituacaoEnvio.Recebendo;

            var resultado = await GravarBlocoAsync(sessao, envio, corpo, aoGravar, cancelar.Token);
            if (resultado is not null) return resultado;

            if (envio.Recebido == envio.Tamanho)
            {
                Finalizar(envio);
                return new(ResultadoGravacao.Concluido, envio.Recebido, Path.GetFileName(envio.CaminhoFinal));
            }
            return new(ResultadoGravacao.Gravado, envio.Recebido);
        }
        finally
        {
            envio.Gravacao.Release();
            // Cancelado enquanto gravava: quem cancelou não conseguiu apagar o .part
            // porque ele estava aberto aqui. Apagamos agora.
            if (envio.Situacao is SituacaoEnvio.Cancelado or SituacaoEnvio.Falhou) TentarApagarParcial(envio);
        }
    }

    private async Task<RespostaGravacao?> GravarBlocoAsync(Sessao sessao, Envio envio, Stream corpo,
        Action? aoGravar, CancellationToken cancelar)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(TamanhoBuffer);
        FileStream? arquivo = null;
        try
        {
            arquivo = new FileStream(envio.CaminhoParcial, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous,
                BufferSize = 0,
            });
            // Depois de uma queda, o que estiver além do último byte confirmado é descartado.
            if (arquivo.Length != envio.Recebido) arquivo.SetLength(envio.Recebido);
            arquivo.Position = envio.Recebido;

            while (true)
            {
                int lidos;
                try
                {
                    lidos = await corpo.ReadAsync(buffer.AsMemory(0, TamanhoBuffer), cancelar);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException
                                               or Microsoft.AspNetCore.Http.BadHttpRequestException)
                {
                    if (envio.Situacao == SituacaoEnvio.Cancelado || !sessao.Ativa)
                        return new(ResultadoGravacao.Cancelado, envio.Recebido);
                    return new(ResultadoGravacao.Interrompido, envio.Recebido);
                }
                if (lidos == 0) return null;

                if (envio.Recebido + lidos > envio.Tamanho)
                    return new(ResultadoGravacao.ConteudoDemais, envio.Recebido);

                try
                {
                    await arquivo.WriteAsync(buffer.AsMemory(0, lidos), CancellationToken.None);
                }
                catch (IOException ex)
                {
                    return new(ResultadoGravacao.FalhaNoDisco, envio.Recebido, Erro: ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return new(ResultadoGravacao.FalhaNoDisco, envio.Recebido, Erro: ex.Message);
                }

                envio.Recebido += lidos;
                RegistrarVelocidade(envio, lidos);
                aoGravar?.Invoke();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Abrir o .part falhou (apagado por fora, disco removido...).
            return new(ResultadoGravacao.FalhaNoDisco, envio.Recebido, Erro: ex.Message);
        }
        finally
        {
            if (arquivo is not null) await arquivo.DisposeAsync();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public bool Pausar(Sessao sessao, string id)
    {
        var envio = Obter(sessao, id);
        if (envio is null || envio.Terminado) return false;
        envio.Situacao = SituacaoEnvio.Pausado;
        envio.Velocidade = 0;
        return true;
    }

    /// <summary>Cancela e apaga o que já chegou. Um envio concluído não é desfeito.</summary>
    public bool Cancelar(Sessao sessao, string id)
    {
        var envio = Obter(sessao, id);
        if (envio is null || envio.Situacao == SituacaoEnvio.Concluido) return false;
        Descartar(envio, SituacaoEnvio.Cancelado, null);
        return true;
    }

    /// <summary>A sessão terminou: tudo o que estava pela metade é apagado.</summary>
    public void AbandonarSessao(Sessao sessao)
    {
        foreach (var envio in _envios.Values)
            if (envio.SessaoToken == sessao.Token && !envio.Terminado)
                Descartar(envio, SituacaoEnvio.Cancelado, "Sessão encerrada antes do fim do envio.");
    }

    /// <summary>Tira da lista o que já terminou (concluído, cancelado ou com falha).</summary>
    public void LimparTerminados()
    {
        foreach (var envio in _envios.Values)
            if (envio.Terminado) _envios.TryRemove(envio.Id, out _);
    }

    public void Dispose()
    {
        foreach (var envio in _envios.Values)
            if (!envio.Terminado) Descartar(envio, SituacaoEnvio.Cancelado, "Aplicativo fechado.");
    }

    private void Descartar(Envio envio, SituacaoEnvio situacao, string? erro)
    {
        envio.Situacao = situacao;
        envio.Erro = erro;
        envio.Velocidade = 0;
        envio.Cancelamento.Cancel();
        // Se ninguém está gravando, apaga já; senão, quem grava apaga ao soltar o arquivo.
        if (envio.Gravacao.Wait(0))
        {
            try { TentarApagarParcial(envio); }
            finally { envio.Gravacao.Release(); }
        }
    }

    private void Finalizar(Envio envio)
    {
        var pasta = _pastaDoEnvio.GetValueOrDefault(envio.Id, PastaDestino);
        lock (_travaNomes)
        {
            for (var tentativa = 0; ; tentativa++)
            {
                var destino = NomesDeArquivo.CaminhoLivre(pasta, envio.NomeSeguro);
                try
                {
                    File.Move(envio.CaminhoParcial, destino, overwrite: false);
                    envio.CaminhoFinal = destino;
                    break;
                }
                catch (IOException) when (tentativa < 5 && File.Exists(destino))
                {
                    // Outro programa criou o mesmo nome entre a escolha e a renomeação.
                }
            }
        }
        envio.Situacao = SituacaoEnvio.Concluido;
        envio.Velocidade = 0;
        ApagarPastaParciaisSeVazia(pasta);
        EnvioConcluido?.Invoke(envio);
    }

    private void RegistrarVelocidade(Envio envio, int bytes)
    {
        var agora = _relogio.GetUtcNow();
        envio.UltimaGravacao = agora;
        envio.AmostraBytes += bytes;
        var decorrido = (agora - envio.AmostraInicio).TotalSeconds;
        if (decorrido >= 1)
        {
            envio.Velocidade = envio.AmostraBytes / decorrido;
            envio.AmostraBytes = 0;
            envio.AmostraInicio = agora;
        }
    }

    private void TentarApagarParcial(Envio envio)
    {
        try { File.Delete(envio.CaminhoParcial); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        ApagarPastaParciaisSeVazia(_pastaDoEnvio.GetValueOrDefault(envio.Id, PastaDestino));
    }

    private static void ApagarPastaParciaisSeVazia(string pasta)
    {
        var parciais = Path.Combine(pasta, PastaParciais);
        try
        {
            if (Directory.Exists(parciais) && !Directory.EnumerateFileSystemEntries(parciais).Any())
                Directory.Delete(parciais);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Valida a pasta e apaga restos de execuções anteriores: os tokens mudam a cada
    /// execução, então nenhum .part antigo pode ser retomado.
    /// </summary>
    private static string PrepararPasta(string pasta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pasta);
        var completa = Path.GetFullPath(pasta);
        if (!Directory.Exists(completa)) throw new DirectoryNotFoundException($"Pasta não encontrada: {completa}");

        var parciais = Path.Combine(completa, PastaParciais);
        if (Directory.Exists(parciais))
        {
            foreach (var resto in Directory.EnumerateFiles(parciais, "*.part"))
            {
                try { File.Delete(resto); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            ApagarPastaParciaisSeVazia(completa);
        }
        return completa;
    }

    private static long? EspacoLivre(string pasta)
    {
        try
        {
            var raiz = Path.GetPathRoot(pasta);
            if (string.IsNullOrEmpty(raiz) || raiz.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return new DriveInfo(raiz).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public long? EspacoLivreNoDestino() => EspacoLivre(PastaDestino);
}
