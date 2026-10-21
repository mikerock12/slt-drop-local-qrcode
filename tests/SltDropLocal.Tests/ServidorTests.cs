using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using SltDropLocal.Core.Envios;
using SltDropLocal.Core.Servidor;
using SltDropLocal.Core.Sessoes;

namespace SltDropLocal.Tests;

/// <summary>
/// Servidor de verdade (Kestrel em 127.0.0.1, porta livre) e HTTP de verdade, como o celular faz.
/// </summary>
public sealed class ServidorTests : IAsyncLifetime
{
    private const int Bloco = 64 * 1024;
    private readonly string _pasta = Directory.CreateTempSubdirectory("sltdrop-servidor-").FullName;
    private readonly FakeTimeProvider _relogio = new(DateTimeOffset.UtcNow);
    private GerenciadorDeSessoes _sessoes = null!;
    private GerenciadorDeEnvios _envios = null!;
    private ServidorDrop _servidor = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _sessoes = new GerenciadorDeSessoes(_relogio);
        _envios = new GerenciadorDeEnvios(_pasta, _relogio);
        _servidor = new ServidorDrop(_sessoes, _envios, new OpcoesServidor
        {
            PortaPreferida = 0, TamanhoBloco = Bloco, Escuta = IPAddress.Loopback, NomeDoPc = "PC-TESTE",
        });
        await _servidor.IniciarAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_servidor.Porta}") };
        await Entrar();
    }

    /// <summary>Digita o código de acesso da sessão atual, como o celular faz; a chave fica no cookie do cliente.</summary>
    private async Task Entrar(HttpClient? cliente = null)
    {
        var r = await (cliente ?? _http).PostAsJsonAsync(Api("/acesso"), new { codigo = _sessoes.Atual.CodigoAcesso });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private string CodigoErrado() => _sessoes.Atual.CodigoAcesso == "Ab3$Cd4%" ? "Zx9!Yw8@" : "Ab3$Cd4%";

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _servidor.DisposeAsync();
        _envios.Dispose();
        _sessoes.Dispose();
        try { Directory.Delete(_pasta, true); } catch (IOException) { }
    }

    private string Api(string caminho = "") => $"/s/{_sessoes.Atual.Token}/api{caminho}";

    private async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> CriarEnvio(string nome, long tamanho)
    {
        var r = await _http.PostAsJsonAsync(Api("/envios"), new { nome, tamanho });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await Json(r)).GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> Put(string id, long posicao, ReadOnlyMemory<byte> bytes) =>
        _http.PutAsync(Api($"/envios/{id}?posicao={posicao}"), new ReadOnlyMemoryContent(bytes));

    private async Task<JsonElement> EnviarTudo(string id, byte[] dados, long inicio = 0)
    {
        JsonElement ultimo = default;
        for (var pos = inicio; pos < dados.Length; pos += Bloco)
        {
            var r = await Put(id, pos, dados.AsMemory((int)pos, (int)Math.Min(Bloco, dados.Length - pos)));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            ultimo = await Json(r);
        }
        return ultimo;
    }

    [Fact]
    public async Task Pagina_abre_com_token_valido_e_tem_cabecalhos_de_seguranca()
    {
        var r = await _http.GetAsync($"/s/{_sessoes.Atual.Token}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("SLT Drop Local QRCode", await r.Content.ReadAsStringAsync());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.False(r.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Estado_informa_pc_pasta_bloco_e_tempo_restante()
    {
        var j = await Json(await _http.GetAsync(Api("/estado")));
        Assert.Equal("PC-TESTE", j.GetProperty("nomePc").GetString());
        Assert.Equal(Path.GetFileName(_pasta), j.GetProperty("pasta").GetString());
        Assert.Equal(Bloco, j.GetProperty("tamanhoBloco").GetInt32());
        Assert.Equal(900, j.GetProperty("segundosRestantes").GetInt32());
    }

    [Fact]
    public async Task Envio_em_varios_blocos_gera_arquivo_identico()
    {
        var dados = RandomNumberGenerator.GetBytes(Bloco * 7 + 1234);
        var id = await CriarEnvio("video.mp4", dados.Length);

        var fim = await EnviarTudo(id, dados);

        Assert.True(fim.GetProperty("concluido").GetBoolean());
        Assert.Equal("video.mp4", fim.GetProperty("nomeFinal").GetString());
        Assert.Equal(dados, await File.ReadAllBytesAsync(Path.Combine(_pasta, "video.mp4")));
        Assert.False(Directory.Exists(Path.Combine(_pasta, GerenciadorDeEnvios.PastaParciais)));
    }

    [Fact]
    public async Task Bloco_fora_de_ordem_eh_recusado_com_a_posicao_certa()
    {
        var dados = RandomNumberGenerator.GetBytes(Bloco * 3);
        var id = await CriarEnvio("a.bin", dados.Length);
        await Put(id, 0, dados.AsMemory(0, Bloco));

        var r = await Put(id, Bloco * 2, dados.AsMemory(Bloco * 2, Bloco));

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("posicao", j.GetProperty("erro").GetString());
        Assert.Equal(Bloco, j.GetProperty("recebido").GetInt64());
    }

    [Fact]
    public async Task Envio_interrompido_no_meio_do_bloco_retoma_de_onde_parou()
    {
        // Simula a pausa: o celular aborta a requisição no meio de um bloco grande.
        var dados = RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        var id = await CriarEnvio("pausado.bin", dados.Length);

        using var cancelar = new CancellationTokenSource();
        var conteudo = new ConteudoQueTrava(dados.AsMemory(0, 2 * 1024 * 1024), pararDepoisDe: 1024 * 1024, cancelar);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            _http.PutAsync(Api($"/envios/{id}?posicao=0"), conteudo, cancelar.Token));

        // O servidor guarda o que chegou; o cliente pergunta e continua dali.
        long recebido = 0;
        for (var i = 0; i < 50; i++)
        {
            var estado = await Json(await _http.GetAsync(Api($"/envios/{id}")));
            recebido = estado.GetProperty("recebido").GetInt64();
            if (recebido > 0) break;
            await Task.Delay(50);
        }
        Assert.InRange(recebido, 1, 1024 * 1024);

        // Pode haver uma requisição velha ainda soltando o arquivo: o servidor responde 409 "ocupado".
        HttpResponseMessage r;
        var tentativas = 0;
        do
        {
            r = await Put(id, recebido, dados.AsMemory((int)recebido));
            if (r.StatusCode == HttpStatusCode.Conflict)
            {
                recebido = (await Json(r)).GetProperty("recebido").GetInt64();
                await Task.Delay(50);
            }
        } while (r.StatusCode == HttpStatusCode.Conflict && ++tentativas < 40);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await Json(r)).GetProperty("concluido").GetBoolean());
        Assert.Equal(dados, await File.ReadAllBytesAsync(Path.Combine(_pasta, "pausado.bin")));
    }

    [Fact]
    public async Task Cancelar_apaga_o_parcial_e_recusa_novos_blocos()
    {
        var dados = RandomNumberGenerator.GetBytes(Bloco * 4);
        var id = await CriarEnvio("cancelado.bin", dados.Length);
        await Put(id, 0, dados.AsMemory(0, Bloco));
        var parciais = Path.Combine(_pasta, GerenciadorDeEnvios.PastaParciais);
        Assert.Single(Directory.GetFiles(parciais));

        var r = await _http.DeleteAsync(Api($"/envios/{id}"));
        Assert.True((await Json(r)).GetProperty("cancelado").GetBoolean());

        Assert.False(Directory.Exists(parciais));
        var depois = await Put(id, Bloco, dados.AsMemory(Bloco, Bloco));
        Assert.Equal(HttpStatusCode.Conflict, depois.StatusCode);
        Assert.Equal("cancelado", (await Json(depois)).GetProperty("erro").GetString());
        Assert.False(File.Exists(Path.Combine(_pasta, "cancelado.bin")));
    }

    [Fact]
    public async Task Pausa_eh_informada_ao_pc()
    {
        var id = await CriarEnvio("p.bin", Bloco * 2);
        await _http.PostAsync(Api($"/envios/{id}/pausa"), null);
        Assert.Equal(SituacaoEnvio.Pausado, _envios.Lista().Single().Situacao);
    }

    [Fact]
    public async Task Nomes_repetidos_nao_sobrescrevem()
    {
        foreach (var conteudo in new[] { "um", "dois", "tres" })
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(conteudo);
            var id = await CriarEnvio("nota.txt", bytes.Length);
            await EnviarTudo(id, bytes);
        }
        Assert.Equal("um", File.ReadAllText(Path.Combine(_pasta, "nota.txt")));
        Assert.Equal("dois", File.ReadAllText(Path.Combine(_pasta, "nota (2).txt")));
        Assert.Equal("tres", File.ReadAllText(Path.Combine(_pasta, "nota (3).txt")));
    }

    [Fact]
    public async Task Nome_com_caminho_nao_sai_da_pasta()
    {
        var id = await CriarEnvio("../../fora.txt", 2);
        await EnviarTudo(id, "ok"u8.ToArray());
        Assert.True(File.Exists(Path.Combine(_pasta, "fora.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_pasta)!, "fora.txt")));
    }

    [Fact]
    public async Task Arquivo_vazio_conclui_na_hora()
    {
        var r = await _http.PostAsJsonAsync(Api("/envios"), new { nome = "vazio.txt", tamanho = 0 });
        Assert.True((await Json(r)).GetProperty("concluido").GetBoolean());
        Assert.Equal(0, new FileInfo(Path.Combine(_pasta, "vazio.txt")).Length);
    }

    [Fact]
    public async Task Mais_bytes_que_o_declarado_eh_recusado()
    {
        var id = await CriarEnvio("x.bin", 10);
        var r = await Put(id, 0, new byte[20]);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("excesso", (await Json(r)).GetProperty("erro").GetString());
    }

    [Fact]
    public async Task Sem_espaco_no_disco_avisa_antes_de_comecar()
    {
        var r = await _http.PostAsJsonAsync(Api("/envios"), new { nome = "enorme.bin", tamanho = long.MaxValue / 2 });
        Assert.Equal(HttpStatusCode.InsufficientStorage, r.StatusCode);
        Assert.Equal("sem-espaco", (await Json(r)).GetProperty("erro").GetString());
    }

    [Fact]
    public async Task Encerrar_no_celular_invalida_o_link_e_descarta_o_incompleto()
    {
        var tokenAntigo = _sessoes.Atual.Token;
        var id = await CriarEnvio("meio.bin", Bloco * 3);
        await Put(id, 0, new byte[Bloco]);

        var r = await _http.PostAsync(Api("/encerrar"), null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.NotEqual(tokenAntigo, _sessoes.Atual.Token);
        var pagina = await _http.GetAsync($"/s/{tokenAntigo}");
        Assert.Equal(HttpStatusCode.Gone, pagina.StatusCode);
        Assert.Contains("expirou", await pagina.Content.ReadAsStringAsync());
        var api = await _http.GetAsync($"/s/{tokenAntigo}/api/estado");
        Assert.Equal(HttpStatusCode.Gone, api.StatusCode);
        var put = await _http.PutAsync($"/s/{tokenAntigo}/api/envios/{id}?posicao={Bloco}", new ByteArrayContent(new byte[Bloco]));
        Assert.Equal(HttpStatusCode.Gone, put.StatusCode);

        Assert.False(Directory.Exists(Path.Combine(_pasta, GerenciadorDeEnvios.PastaParciais)));
        Assert.Equal(SituacaoEnvio.Cancelado, _envios.Lista().Single().Situacao);
        // A sessão nova funciona, depois do código novo.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync(Api("/estado"))).StatusCode);
        await Entrar();
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync(Api("/estado"))).StatusCode);
    }

    [Fact]
    public async Task Quinze_minutos_sem_atividade_expiram_a_sessao()
    {
        var tokenAntigo = _sessoes.Atual.Token;
        var id = await CriarEnvio("parado.bin", Bloco * 2);
        await Put(id, 0, new byte[Bloco]);

        _relogio.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        Assert.NotEqual(tokenAntigo, _sessoes.Atual.Token);
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync($"/s/{tokenAntigo}/api/estado")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync($"/s/{tokenAntigo}")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_pasta, GerenciadorDeEnvios.PastaParciais)));
    }

    [Fact]
    public async Task Consultar_estado_nao_conta_como_atividade()
    {
        _relogio.Advance(TimeSpan.FromMinutes(10));
        await _http.GetAsync(Api("/estado"));
        var j = await Json(await _http.GetAsync(Api("/estado")));
        Assert.Equal(300, j.GetProperty("segundosRestantes").GetInt32());
    }

    [Fact]
    public async Task Sem_o_codigo_a_pagina_abre_mas_a_api_nao()
    {
        using var semCodigo = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await semCodigo.GetAsync($"/s/{_sessoes.Atual.Token}")).StatusCode);

        foreach (var r in new[]
        {
            await semCodigo.GetAsync(Api("/estado")),
            await semCodigo.PostAsJsonAsync(Api("/envios"), new { nome = "x", tamanho = 1 }),
            await semCodigo.PostAsync(Api("/encerrar"), null),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("codigo", (await Json(r)).GetProperty("erro").GetString());
        }
        Assert.True(_sessoes.Atual.Ativa);
    }

    [Fact]
    public async Task Chave_vai_em_cookie_httponly_restrito_a_sessao()
    {
        using var cliente = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = _http.BaseAddress };
        var r = await cliente.PostAsJsonAsync(Api("/acesso"), new { codigo = _sessoes.Atual.CodigoAcesso });
        var cookie = r.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"path=/s/{_sessoes.Atual.Token}", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Codigo_errado_informa_tentativas_e_a_quinta_encerra_a_sessao()
    {
        var token = _sessoes.Atual.Token;
        for (var restantes = 4; restantes >= 1; restantes--)
        {
            var r = await _http.PostAsJsonAsync(Api("/acesso"), new { codigo = CodigoErrado() });
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            var j = await Json(r);
            Assert.Equal("codigo-incorreto", j.GetProperty("erro").GetString());
            Assert.Equal(restantes, j.GetProperty("restantes").GetInt32());
        }

        var ultima = await _http.PostAsJsonAsync($"/s/{token}/api/acesso", new { codigo = CodigoErrado() });
        Assert.Equal(HttpStatusCode.Gone, ultima.StatusCode);
        Assert.Equal("tentativas", (await Json(ultima)).GetProperty("erro").GetString());
        Assert.NotEqual(token, _sessoes.Atual.Token);
        // A chave que o cliente já tinha também morreu com a sessão.
        Assert.Equal(HttpStatusCode.Gone, (await _http.GetAsync($"/s/{token}/api/estado")).StatusCode);
    }

    [Fact]
    public async Task Leitor_de_qr_eh_servido_sem_token_e_com_cache()
    {
        using var semCodigo = new HttpClient { BaseAddress = _http.BaseAddress };
        var r = await semCodigo.GetAsync("/recursos/jsqr.js");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/javascript", r.Content.Headers.ContentType?.MediaType);
        Assert.Contains("max-age", r.Headers.CacheControl?.ToString());
        Assert.Contains("jsQR", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Link_inventado_da_404()
    {
        var r = await _http.GetAsync("/s/inventado");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/s/inventado/api/estado")).StatusCode);
    }

    [Fact]
    public async Task Outro_aparelho_nao_usa_a_sessao_de_quem_chegou_primeiro()
    {
        // O primeiro aparelho (127.0.0.1) já digitou o código e é o dono da sessão.

        // O "segundo aparelho" sai de 127.0.0.2.
        using var outro = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
                await s.ConnectAsync(ctx.DnsEndPoint, ct);
                return new NetworkStream(s, ownsSocket: true);
            },
        }) { BaseAddress = _http.BaseAddress };

        var r = await outro.GetAsync($"/s/{_sessoes.Atual.Token}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outro.PostAsJsonAsync(Api("/envios"), new { nome = "x", tamanho = 1 })).StatusCode);
        // Nem sabendo o código o outro aparelho entra.
        var codigo = await outro.PostAsJsonAsync(Api("/acesso"), new { codigo = _sessoes.Atual.CodigoAcesso });
        Assert.Equal(HttpStatusCode.Forbidden, codigo.StatusCode);
    }

    /// <summary>
    /// Arquivo maior que 4 GB, para provar que nada usa int nem carrega o arquivo inteiro.
    /// Lento (grava e lê ~4,5 GB): só roda com SLTDROP_TESTE_GRANDE=1.
    /// </summary>
    [Fact]
    public async Task Arquivo_maior_que_4GB()
    {
        if (Environment.GetEnvironmentVariable("SLTDROP_TESTE_GRANDE") != "1") return;

        const long tamanho = 4L * 1024 * 1024 * 1024 + 123_457;
        const int blocoGrande = 8 * 1024 * 1024;
        var id = await CriarEnvio("grande.bin", tamanho);
        var buffer = new byte[blocoGrande];
        using var hashEnviado = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        for (long pos = 0; pos < tamanho; pos += blocoGrande)
        {
            var n = (int)Math.Min(blocoGrande, tamanho - pos);
            Preencher(buffer.AsSpan(0, n), pos);
            hashEnviado.AppendData(buffer, 0, n);
            var r = await Put(id, pos, buffer.AsMemory(0, n));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }

        var final = Path.Combine(_pasta, "grande.bin");
        Assert.Equal(tamanho, new FileInfo(final).Length);
        await using var f = File.OpenRead(final);
        Assert.Equal(hashEnviado.GetHashAndReset(), await SHA256.HashDataAsync(f));

        static void Preencher(Span<byte> destino, long posicao)
        {
            for (var i = 0; i < destino.Length; i++)
                destino[i] = (byte)((posicao + i) * 2654435761L >> 13);
        }
    }

    /// <summary>Manda parte dos bytes e então trava, até o teste cancelar a requisição.</summary>
    private sealed class ConteudoQueTrava(ReadOnlyMemory<byte> dados, int pararDepoisDe, CancellationTokenSource cancelar) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(dados[..pararDepoisDe]);
            await stream.FlushAsync();
            await Task.Delay(300);
            cancelar.Cancel();
            await Task.Delay(Timeout.Infinite, cancelar.Token);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = dados.Length;
            return true;
        }
    }
}
