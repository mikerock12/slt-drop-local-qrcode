using System.Net;
using System.Reflection;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SltDropLocal.Core.Envios;
using SltDropLocal.Core.Sessoes;

namespace SltDropLocal.Core.Servidor;

public sealed class OpcoesServidor
{
    /// <summary>Porta tentada primeiro; se estiver ocupada, as 19 seguintes, e por fim uma livre qualquer. 0 = qualquer livre.</summary>
    public int PortaPreferida { get; init; } = 8770;

    /// <summary>Tamanho dos blocos que o celular manda. Pausa e retomada acontecem na fronteira deles.</summary>
    public int TamanhoBloco { get; init; } = 8 * 1024 * 1024;

    public string NomeDoPc { get; init; } = Environment.MachineName;

    /// <summary>Endereço de escuta. O padrão atende todas as placas de rede (IPv4).</summary>
    public IPAddress Escuta { get; init; } = IPAddress.Any;
}

/// <summary>
/// O servidor HTTP da rede local. Tudo fica sob /s/{token}/: sem o token da sessão
/// atual não há página nem API. A página abre só com o token, mas a API exige
/// antes o código de acesso mostrado no PC (que devolve uma chave em cookie).
/// O celular usa:
///   GET    /s/{t}                 página de envio (pede o código)
///   POST   /s/{t}/api/acesso      confere o código {codigo}; 5 erros encerram a sessão
///   GET    /s/{t}/api/estado      tempo até expirar, nome do PC, tamanho do bloco
///   POST   /s/{t}/api/envios      começa um arquivo {nome, tamanho}
///   GET    /s/{t}/api/envios/{id} de onde continuar
///   PUT    /s/{t}/api/envios/{id}?posicao=N   um bloco de bytes
///   POST   /s/{t}/api/envios/{id}/pausa
///   DELETE /s/{t}/api/envios/{id} cancela e apaga o que chegou
///   POST   /s/{t}/api/encerrar    encerra a sessão (o PC gera outro QR Code)
/// </summary>
public sealed class ServidorDrop : IAsyncDisposable
{
    private const string NomeCookie = "sltdrop_chave";

    private readonly GerenciadorDeSessoes _sessoes;
    private readonly GerenciadorDeEnvios _envios;
    private readonly OpcoesServidor _opcoes;
    private readonly string _pagina = LerRecurso("SltDropLocal.pagina.html");
    private readonly string _modeloAviso = LerRecurso("SltDropLocal.aviso.html");
    private readonly string _jsQr = LerRecurso("SltDropLocal.jsQR.js");
    private WebApplication? _app;

    public ServidorDrop(GerenciadorDeSessoes sessoes, GerenciadorDeEnvios envios, OpcoesServidor? opcoes = null)
    {
        _sessoes = sessoes;
        _envios = envios;
        _opcoes = opcoes ?? new OpcoesServidor();
        _sessoes.SessaoTrocada += AoTrocarSessao;
    }

    public int Porta { get; private set; }

    public async Task IniciarAsync(CancellationToken cancelar = default)
    {
        if (_app is not null) throw new InvalidOperationException("Servidor já iniciado.");

        // Porta 0 = qualquer uma livre, escolhida pelo Windows.
        var portas = _opcoes.PortaPreferida == 0
            ? [0]
            : Enumerable.Range(_opcoes.PortaPreferida, 20).Where(p => p is > 0 and < 65536).Append(0);
        Exception? ultimoErro = null;
        foreach (var porta in portas)
        {
            var app = Construir(porta);
            try
            {
                await app.StartAsync(cancelar);
                _app = app;
                Porta = LerPortaReal(app) ?? porta;
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                ultimoErro = ex;
                await app.DisposeAsync();
            }
        }
        throw new IOException("Não foi possível abrir nenhuma porta para o servidor.", ultimoErro);
    }

    public string Endereco(IPAddress ip, Sessao sessao) => $"http://{ip}:{Porta}/s/{sessao.Token}";

    public async ValueTask DisposeAsync()
    {
        _sessoes.SessaoTrocada -= AoTrocarSessao;
        if (_app is not null)
        {
            using (var limite = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                await _app.StopAsync(limite.Token);
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private void AoTrocarSessao(Sessao antiga, MotivoEncerramento motivo, Sessao nova) => _envios.AbandonarSessao(antiga);

    private WebApplication Construir(int porta)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ServidorDrop).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Listen(_opcoes.Escuta, porta);
            // Limite por requisição, não por arquivo: os arquivos vêm em blocos.
            k.Limits.MaxRequestBodySize = Math.Max(_opcoes.TamanhoBloco * 4L, 32L * 1024 * 1024);
        });

        var app = builder.Build();
        app.Use(async (ctx, proximo) =>
        {
            var h = ctx.Response.Headers;
            h.CacheControl = "no-store";
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            // O token está na URL: não pode vazar em Referer para lugar nenhum.
            h["Referrer-Policy"] = "no-referrer";
            await proximo(ctx);
        });

        app.MapGet("/", () => Aviso(StatusCodes.Status200OK, "SLT Drop Local QRCode",
            "Para enviar arquivos, escaneie o QR Code que aparece na tela do PC."));
        app.MapGet("/favicon.ico", () => Results.NoContent());
        // Biblioteca pública (jsQR) que lê o QR do código de acesso na foto. Não tem
        // nada da sessão, então pode ficar em cache no celular.
        app.MapGet("/recursos/jsqr.js", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "public, max-age=86400";
            return Results.Content(_jsQr, "text/javascript; charset=utf-8");
        });
        app.MapGet("/s/{token}", Pagina); // também atende "/s/{token}/"

        var api = app.MapGroup("/s/{token}/api");
        api.MapPost("/acesso", VerificarCodigo);
        api.MapGet("/estado", Estado);
        api.MapPost("/envios", CriarEnvio);
        api.MapGet("/envios/{id}", ConsultarEnvio);
        api.MapPut("/envios/{id}", GravarBloco);
        api.MapPost("/envios/{id}/pausa", PausarEnvio);
        api.MapDelete("/envios/{id}", CancelarEnvio);
        api.MapPost("/encerrar", Encerrar);
        return app;
    }

    private IResult Pagina(string token, HttpContext ctx)
    {
        switch (_sessoes.AbrirPagina(token, ctx.Connection.RemoteIpAddress, out var sessao))
        {
            case Acesso.Permitido:
                _sessoes.RegistrarAtividade(sessao!);
                return Results.Content(_pagina, "text/html; charset=utf-8");
            case Acesso.Expirada:
                return Aviso(StatusCodes.Status410Gone, "Sessão encerrada",
                    "Este QR Code já expirou e não pode mais ser usado. Escaneie o QR Code novo que está na tela do PC.");
            case Acesso.OutroAparelho:
                return Aviso(StatusCodes.Status403Forbidden, "Sessão em uso",
                    "Este QR Code já está sendo usado por outro aparelho. No PC, clique em \"Gerar novo QR Code\" para começar outra sessão.");
            default:
                return Aviso(StatusCodes.Status404NotFound, "Link inválido",
                    "Este link não é de uma sessão válida. Escaneie o QR Code que está na tela do PC.");
        }
    }

    private IResult Estado(string token, HttpContext ctx)
    {
        if (Negado(token, ctx, out var sessao) is { } negado) return negado;
        return Results.Json(new
        {
            ativa = true,
            segundosRestantes = (int)Math.Ceiling(_sessoes.TempoAteExpirar(sessao).TotalSeconds),
            limiteSegundos = (int)_sessoes.LimiteInatividade.TotalSeconds,
            nomePc = _opcoes.NomeDoPc,
            pasta = Path.GetFileName(_envios.PastaDestino.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } nome
                ? nome : _envios.PastaDestino,
            espacoLivre = _envios.EspacoLivreNoDestino(),
            tamanhoBloco = _opcoes.TamanhoBloco,
        });
    }

    public sealed record NovoEnvio(string? Nome, long Tamanho);

    private IResult CriarEnvio(string token, NovoEnvio pedido, HttpContext ctx)
    {
        if (Negado(token, ctx, out var sessao) is { } negado) return negado;
        _sessoes.RegistrarAtividade(sessao);
        if (pedido.Tamanho < 0) return Erro(StatusCodes.Status400BadRequest, "tamanho");

        try
        {
            var envio = _envios.Criar(sessao, pedido.Nome, pedido.Tamanho);
            return Results.Json(DescreverEnvio(envio));
        }
        catch (EspacoInsuficienteException ex)
        {
            return Results.Json(new { erro = "sem-espaco", livre = ex.Livre }, statusCode: StatusCodes.Status507InsufficientStorage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Json(new { erro = "disco", mensagem = ex.Message }, statusCode: StatusCodes.Status507InsufficientStorage);
        }
    }

    private IResult ConsultarEnvio(string token, string id, HttpContext ctx)
    {
        if (Negado(token, ctx, out var sessao) is { } negado) return negado;
        var envio = _envios.Obter(sessao, id);
        return envio is null ? Erro(StatusCodes.Status404NotFound, "envio") : Results.Json(DescreverEnvio(envio));
    }

    private async Task<IResult> GravarBloco(string token, string id, long posicao, HttpContext ctx)
    {
        if (Negado(token, ctx, out var sessao) is { } negado) return negado;
        _sessoes.RegistrarAtividade(sessao);
        var envio = _envios.Obter(sessao, id);
        if (envio is null) return Erro(StatusCodes.Status404NotFound, "envio");

        var r = await _envios.GravarAsync(sessao, envio, posicao, ctx.Request.Body,
            () => _sessoes.RegistrarAtividade(sessao), ctx.RequestAborted);

        if (!sessao.Ativa) return Erro(StatusCodes.Status410Gone, "expirada");
        return r.Resultado switch
        {
            ResultadoGravacao.Gravado => Results.Json(new { recebido = r.Recebido, concluido = false }),
            ResultadoGravacao.Concluido => Results.Json(new { recebido = r.Recebido, concluido = true, nomeFinal = r.NomeFinal }),
            ResultadoGravacao.PosicaoErrada => Results.Json(new { erro = "posicao", recebido = r.Recebido }, statusCode: StatusCodes.Status409Conflict),
            ResultadoGravacao.Ocupado => Results.Json(new { erro = "ocupado", recebido = r.Recebido }, statusCode: StatusCodes.Status409Conflict),
            ResultadoGravacao.Cancelado => Results.Json(new { erro = "cancelado", recebido = r.Recebido }, statusCode: StatusCodes.Status409Conflict),
            ResultadoGravacao.ConteudoDemais => Results.Json(new { erro = "excesso", recebido = r.Recebido }, statusCode: StatusCodes.Status400BadRequest),
            ResultadoGravacao.FalhaNoDisco => Results.Json(new { erro = "disco", mensagem = r.Erro, recebido = r.Recebido }, statusCode: StatusCodes.Status507InsufficientStorage),
            _ => Results.Json(new { erro = "interrompido", recebido = r.Recebido }, statusCode: StatusCodes.Status400BadRequest),
        };
    }

    private IResult PausarEnvio(string token, string id, HttpContext ctx)
    {
        if (Negado(token, ctx, out var sessao) is { } negado) return negado;
        _sessoes.RegistrarAtividade(sessao);
        _envios.Pausar(sessao, id);
        return Results.Json(new { ok = true });
    }

    private IResult CancelarEnvio(string token, string id, HttpContext ctx)
    {
        if (Negado(token, ctx, out var sessao) is { } negado) return negado;
        _sessoes.RegistrarAtividade(sessao);
        return Results.Json(new { cancelado = _envios.Cancelar(sessao, id) });
    }

    private IResult Encerrar(string token, HttpContext ctx)
    {
        if (Negado(token, ctx, out var sessao) is { } negado) return negado;
        _sessoes.Encerrar(MotivoEncerramento.EncerradaNoCelular, somenteSe: sessao);
        return Results.Json(new { encerrada = true });
    }

    public sealed record PedidoAcesso(string? Codigo);

    private IResult VerificarCodigo(string token, PedidoAcesso pedido, HttpContext ctx)
    {
        var r = _sessoes.VerificarCodigo(token, ctx.Connection.RemoteIpAddress, pedido.Codigo);
        switch (r.Situacao)
        {
            case SituacaoCodigo.Aceito:
                // A chave vai em cookie HttpOnly, restrito ao caminho desta sessão:
                // o JavaScript da página nunca a vê e ela some com o navegador.
                ctx.Response.Cookies.Append(NomeCookie, r.Chave!, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/s/" + token,
                    IsEssential = true,
                });
                return Results.Json(new { ok = true });
            case SituacaoCodigo.Incorreto:
                return Results.Json(new { erro = "codigo-incorreto", restantes = r.Restantes }, statusCode: StatusCodes.Status401Unauthorized);
            case SituacaoCodigo.Esgotado:
                return Erro(StatusCodes.Status410Gone, "tentativas");
            case SituacaoCodigo.Expirada:
                return Erro(StatusCodes.Status410Gone, "expirada");
            case SituacaoCodigo.OutroAparelho:
                return Erro(StatusCodes.Status403Forbidden, "outro-aparelho");
            default:
                return Erro(StatusCodes.Status404NotFound, "invalida");
        }
    }

    private IResult? Negado(string token, HttpContext ctx, out Sessao sessao)
    {
        var acesso = _sessoes.Autorizar(token, ctx.Connection.RemoteIpAddress,
            ctx.Request.Cookies[NomeCookie], out var s);
        sessao = s!;
        return acesso switch
        {
            Acesso.Permitido => null,
            Acesso.Expirada => Erro(StatusCodes.Status410Gone, "expirada"),
            Acesso.OutroAparelho => Erro(StatusCodes.Status403Forbidden, "outro-aparelho"),
            Acesso.CodigoNecessario => Erro(StatusCodes.Status401Unauthorized, "codigo"),
            _ => Erro(StatusCodes.Status404NotFound, "invalida"),
        };
    }

    private static object DescreverEnvio(Envio e) => new
    {
        id = e.Id,
        recebido = e.Recebido,
        tamanho = e.Tamanho,
        situacao = e.Situacao.ToString().ToLowerInvariant(),
        concluido = e.Situacao == SituacaoEnvio.Concluido,
        nomeFinal = e.CaminhoFinal is null ? null : Path.GetFileName(e.CaminhoFinal),
    };

    private static IResult Erro(int status, string erro) => Results.Json(new { erro }, statusCode: status);

    private IResult Aviso(int status, string titulo, string mensagem)
    {
        var html = _modeloAviso
            .Replace("{{TITULO}}", HtmlEncoder.Default.Encode(titulo))
            .Replace("{{MENSAGEM}}", HtmlEncoder.Default.Encode(mensagem));
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }

    private static int? LerPortaReal(WebApplication app)
    {
        var enderecos = app.Services.GetService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer)) is
            Microsoft.AspNetCore.Hosting.Server.IServer servidor
            ? servidor.Features.Get<IServerAddressesFeature>()?.Addresses
            : null;
        foreach (var e in enderecos ?? [])
            if (Uri.TryCreate(e.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "[::1]"), UriKind.Absolute, out var uri))
                return uri.Port;
        return null;
    }

    private static string LerRecurso(string nome)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(nome)
            ?? throw new InvalidOperationException($"Recurso ausente: {nome}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
