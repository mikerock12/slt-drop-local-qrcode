using System.Net;
using Microsoft.Extensions.Time.Testing;
using SltDropLocal.Core.Sessoes;

namespace SltDropLocal.Tests;

public class GerenciadorDeSessoesTests
{
    private static readonly IPAddress Celular = IPAddress.Parse("192.168.0.20");
    private static readonly IPAddress OutroCelular = IPAddress.Parse("192.168.0.21");

    private static string Entrar(GerenciadorDeSessoes g, IPAddress? ip = null)
    {
        var r = g.VerificarCodigo(g.Atual.Token, ip ?? Celular, g.Atual.CodigoAcesso);
        Assert.Equal(SituacaoCodigo.Aceito, r.Situacao);
        return r.Chave!;
    }

    private static string CodigoErrado(Sessao s) => s.CodigoAcesso == "Ab3$Cd4%" ? "Zx9!Yw8@" : "Ab3$Cd4%";

    [Fact]
    public void Token_tem_256_bits_em_base64url_e_nunca_se_repete()
    {
        var tokens = Enumerable.Range(0, 500).Select(_ => GerenciadorDeSessoes.GerarToken()).ToList();
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void Codigo_de_acesso_tem_8_caracteres_dos_quatro_tipos_e_nunca_se_repete()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var codigos = new List<string>();
        for (var i = 0; i < 3000; i++)
        {
            codigos.Add(g.Atual.CodigoAcesso);
            g.Encerrar(MotivoEncerramento.EncerradaNoPc);
        }
        Assert.All(codigos, c =>
        {
            Assert.Equal(8, c.Length);
            Assert.Contains(c, ch => GerenciadorDeSessoes.Maiusculas.Contains(ch));
            Assert.Contains(c, ch => GerenciadorDeSessoes.Minusculas.Contains(ch));
            Assert.Contains(c, ch => GerenciadorDeSessoes.Digitos.Contains(ch));
            Assert.Contains(c, ch => GerenciadorDeSessoes.Simbolos.Contains(ch));
            // Nada que se confunda na tela.
            Assert.DoesNotContain(c, ch => "IlO0o1 ".Contains(ch));
        });
        Assert.Equal(codigos.Count, codigos.Distinct().Count());
    }

    [Fact]
    public void Simbolos_aparecem_em_posicoes_variadas()
    {
        // O embaralhamento não pode deixar cada tipo sempre na mesma posição.
        var posicoes = Enumerable.Range(0, 400).Select(_ => GerenciadorDeSessoes.NovoCodigo())
            .Select(c => c.IndexOfAny(GerenciadorDeSessoes.Simbolos.ToCharArray())).Distinct().Count();
        Assert.True(posicoes >= 6, $"símbolo só apareceu em {posicoes} posições");
    }

    [Fact]
    public void Codigos_nao_se_repetem_entre_execucoes()
    {
        var arquivo = Path.Combine(Directory.CreateTempSubdirectory("sltdrop-codigos-").FullName, "codigos.txt");
        var primeiros = new List<string>();
        using (var g = new GerenciadorDeSessoes(new FakeTimeProvider(), historico: new HistoricoDeCodigos(arquivo)))
            for (var i = 0; i < 200; i++) { primeiros.Add(g.Atual.CodigoAcesso); g.Encerrar(MotivoEncerramento.EncerradaNoPc); }

        // "Reabre o programa": o histórico em disco impede repetir qualquer um dos anteriores.
        var historico = new HistoricoDeCodigos(arquivo);
        Assert.True(historico.Quantidade >= 200);
        foreach (var c in primeiros) Assert.False(historico.Reservar(c));
        using var g2 = new GerenciadorDeSessoes(new FakeTimeProvider(), historico: historico);
        Assert.DoesNotContain(g2.Atual.CodigoAcesso, primeiros);
    }

    [Fact]
    public void Historico_guarda_no_maximo_o_limite()
    {
        var arquivo = Path.Combine(Directory.CreateTempSubdirectory("sltdrop-limite-").FullName, "codigos.txt");
        File.WriteAllLines(arquivo, Enumerable.Range(0, HistoricoDeCodigos.Limite + 50).Select(i => i.ToString("D7")));
        var h = new HistoricoDeCodigos(arquivo);
        Assert.Equal(HistoricoDeCodigos.Limite, h.Quantidade);
        Assert.True(h.Reservar("0000000")); // o mais antigo saiu do histórico
    }

    [Fact]
    public void Encerrar_troca_token_e_codigo_e_o_antigo_passa_a_ser_expirado_para_sempre()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var antiga = g.Atual;

        var nova = g.Encerrar(MotivoEncerramento.EncerradaNoCelular);

        Assert.NotEqual(antiga.Token, nova.Token);
        Assert.NotEqual(antiga.CodigoAcesso, nova.CodigoAcesso);
        Assert.False(antiga.Ativa);
        Assert.Equal(MotivoEncerramento.EncerradaNoCelular, antiga.Motivo);
        Assert.Equal(Acesso.Expirada, g.AbrirPagina(antiga.Token, Celular, out _));
        Assert.Equal(SituacaoCodigo.Expirada, g.VerificarCodigo(antiga.Token, Celular, antiga.CodigoAcesso).Situacao);
        Assert.Equal(Acesso.Permitido, g.AbrirPagina(nova.Token, Celular, out _));

        for (var i = 0; i < 10; i++) g.Encerrar(MotivoEncerramento.EncerradaNoPc);
        Assert.Equal(Acesso.Expirada, g.AbrirPagina(antiga.Token, Celular, out _));
        Assert.Equal(Acesso.Expirada, g.AbrirPagina(nova.Token, Celular, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Token_desconhecido_eh_invalido(string token)
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        Assert.Equal(Acesso.Invalida, g.AbrirPagina(token, Celular, out var s));
        Assert.Null(s);
        Assert.Equal(SituacaoCodigo.Invalida, g.VerificarCodigo(token, Celular, g.Atual.CodigoAcesso).Situacao);
    }

    [Fact]
    public void Api_exige_o_codigo_antes_de_tudo()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var token = g.Atual.Token;

        Assert.Equal(Acesso.Permitido, g.AbrirPagina(token, Celular, out _));
        Assert.True(g.Atual.PaginaAberta);
        Assert.Equal(Acesso.CodigoNecessario, g.Autorizar(token, Celular, null, out var s));
        Assert.Null(s);
        Assert.Equal(Acesso.CodigoNecessario, g.Autorizar(token, Celular, "chave-inventada", out _));

        var chave = Entrar(g);
        Assert.Equal(Acesso.Permitido, g.Autorizar(token, Celular, chave, out s));
        Assert.Same(g.Atual, s);
        Assert.Equal(Celular, g.Atual.Aparelho);
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("{1} {2}")]
    [InlineData(" {1}  {2} ")]
    public void Codigo_ignora_espacos(string formato)
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var c = g.Atual.CodigoAcesso;
        var digitado = string.Format(formato, c, c[..4], c[4..]);
        Assert.Equal(SituacaoCodigo.Aceito, g.VerificarCodigo(g.Atual.Token, Celular, digitado).Situacao);
    }

    [Fact]
    public void Codigo_diferencia_maiusculas_de_minusculas()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var c = g.Atual.CodigoAcesso;
        var trocado = new string(c.Select(ch => char.IsUpper(ch) ? char.ToLowerInvariant(ch) : char.ToUpperInvariant(ch)).ToArray());
        Assert.Equal(SituacaoCodigo.Incorreto, g.VerificarCodigo(g.Atual.Token, Celular, trocado).Situacao);
        Assert.Equal(SituacaoCodigo.Aceito, g.VerificarCodigo(g.Atual.Token, Celular, c).Situacao);
    }

    [Fact]
    public void Cinco_codigos_errados_encerram_a_sessao()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var sessao = g.Atual;
        MotivoEncerramento? motivo = null;
        g.SessaoTrocada += (_, m, _) => motivo = m;

        for (var restantes = GerenciadorDeSessoes.MaxTentativas - 1; restantes >= 1; restantes--)
        {
            var r = g.VerificarCodigo(sessao.Token, Celular, CodigoErrado(sessao));
            Assert.Equal(SituacaoCodigo.Incorreto, r.Situacao);
            Assert.Equal(restantes, r.Restantes);
        }
        Assert.True(sessao.Ativa);

        Assert.Equal(SituacaoCodigo.Esgotado, g.VerificarCodigo(sessao.Token, Celular, CodigoErrado(sessao)).Situacao);
        Assert.False(sessao.Ativa);
        Assert.Equal(MotivoEncerramento.TentativasEsgotadas, motivo);
        Assert.NotSame(sessao, g.Atual);
        // Nem o código certo vale mais.
        Assert.Equal(SituacaoCodigo.Expirada, g.VerificarCodigo(sessao.Token, Celular, sessao.CodigoAcesso).Situacao);
    }

    [Fact]
    public void Chave_de_uma_sessao_nao_vale_na_seguinte()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var chave = Entrar(g);
        g.Encerrar(MotivoEncerramento.EncerradaNoPc);
        Assert.Equal(Acesso.CodigoNecessario, g.Autorizar(g.Atual.Token, Celular, chave, out _));
    }

    [Fact]
    public void Primeiro_aparelho_que_acerta_o_codigo_fica_com_a_sessao()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var token = g.Atual.Token;

        // Antes do código, qualquer aparelho abre a página.
        Assert.Equal(Acesso.Permitido, g.AbrirPagina(token, OutroCelular, out _));
        var chave = Entrar(g, Celular);

        Assert.Equal(Acesso.Permitido, g.Autorizar(token, IPAddress.Parse("::ffff:192.168.0.20"), chave, out _));
        Assert.Equal(Acesso.OutroAparelho, g.AbrirPagina(token, OutroCelular, out _));
        Assert.Equal(Acesso.OutroAparelho, g.Autorizar(token, OutroCelular, chave, out _));
        Assert.Equal(SituacaoCodigo.OutroAparelho, g.VerificarCodigo(token, OutroCelular, g.Atual.CodigoAcesso).Situacao);
        // Tentativas de outro aparelho não gastam as do dono.
        Assert.Equal(0, g.Atual.TentativasErradas);
    }

    [Fact]
    public void Expira_apos_15_minutos_sem_atividade_e_gera_sessao_nova()
    {
        var relogio = new FakeTimeProvider();
        using var g = new GerenciadorDeSessoes(relogio);
        var antiga = g.Atual;
        (Sessao antiga, MotivoEncerramento motivo, Sessao nova)? troca = null;
        g.SessaoTrocada += (a, m, n) => troca = (a, m, n);

        relogio.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(58));
        Assert.True(antiga.Ativa);
        Assert.Null(troca);

        relogio.Advance(TimeSpan.FromSeconds(3));
        Assert.False(antiga.Ativa);
        Assert.NotNull(troca);
        Assert.Same(antiga, troca.Value.antiga);
        Assert.Equal(MotivoEncerramento.Inatividade, troca.Value.motivo);
        Assert.Same(g.Atual, troca.Value.nova);
        Assert.Equal(Acesso.Expirada, g.AbrirPagina(antiga.Token, Celular, out _));
    }

    [Fact]
    public void Atividade_adia_a_expiracao()
    {
        var relogio = new FakeTimeProvider();
        using var g = new GerenciadorDeSessoes(relogio);
        var sessao = g.Atual;

        for (var i = 0; i < 6; i++)
        {
            relogio.Advance(TimeSpan.FromMinutes(10));
            g.RegistrarAtividade(sessao);
        }
        Assert.True(sessao.Ativa);
        Assert.Equal(TimeSpan.FromMinutes(15), g.TempoAteExpirar(sessao));

        relogio.Advance(TimeSpan.FromMinutes(16));
        Assert.False(sessao.Ativa);
    }

    [Fact]
    public void Limite_de_inatividade_configuravel()
    {
        var relogio = new FakeTimeProvider();
        using var g = new GerenciadorDeSessoes(relogio, TimeSpan.FromSeconds(30));
        var sessao = g.Atual;
        relogio.Advance(TimeSpan.FromSeconds(31));
        Assert.False(sessao.Ativa);
    }

    [Fact]
    public void Encerrar_condicional_nao_derruba_a_sessao_seguinte()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var antiga = g.Atual;
        var nova = g.Encerrar(MotivoEncerramento.EncerradaNoPc);

        // Um "encerrar" atrasado vindo da aba antiga não pode matar a sessão nova.
        g.Encerrar(MotivoEncerramento.EncerradaNoCelular, somenteSe: antiga);
        Assert.True(nova.Ativa);
        Assert.Same(nova, g.Atual);
    }

    [Fact]
    public void Encerrar_cancela_o_token_de_encerramento()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var sessao = g.Atual;
        Assert.False(sessao.Encerrando.IsCancellationRequested);
        g.Encerrar(MotivoEncerramento.EncerradaNoPc);
        Assert.True(sessao.Encerrando.IsCancellationRequested);
    }
}
