using System.Net;
using Microsoft.Extensions.Time.Testing;
using SltDropLocal.Core.Sessoes;

namespace SltDropLocal.Tests;

public class GerenciadorDeSessoesTests
{
    private static readonly IPAddress Celular = IPAddress.Parse("192.168.0.20");
    private static readonly IPAddress OutroCelular = IPAddress.Parse("192.168.0.21");

    [Fact]
    public void Token_tem_256_bits_em_base64url_e_nunca_se_repete()
    {
        var tokens = Enumerable.Range(0, 500).Select(_ => GerenciadorDeSessoes.GerarToken()).ToList();
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void Encerrar_troca_o_token_e_o_antigo_passa_a_ser_expirado_para_sempre()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var antiga = g.Atual;

        var nova = g.Encerrar(MotivoEncerramento.EncerradaNoCelular);

        Assert.NotEqual(antiga.Token, nova.Token);
        Assert.False(antiga.Ativa);
        Assert.Equal(MotivoEncerramento.EncerradaNoCelular, antiga.Motivo);
        Assert.Equal(Acesso.Expirada, g.Autorizar(antiga.Token, Celular, true, out _));
        Assert.Equal(Acesso.Permitido, g.Autorizar(nova.Token, Celular, true, out _));

        // Mesmo depois de várias outras sessões, o token antigo continua recusado.
        for (var i = 0; i < 10; i++) g.Encerrar(MotivoEncerramento.EncerradaNoPc);
        Assert.Equal(Acesso.Expirada, g.Autorizar(antiga.Token, Celular, true, out _));
        Assert.Equal(Acesso.Expirada, g.Autorizar(nova.Token, Celular, true, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Token_desconhecido_eh_invalido(string token)
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        Assert.Equal(Acesso.Invalida, g.Autorizar(token, Celular, true, out var s));
        Assert.Null(s);
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
        Assert.Equal(Acesso.Expirada, g.Autorizar(antiga.Token, Celular, true, out _));
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
    public void Primeiro_aparelho_reivindica_a_sessao_e_outro_eh_recusado()
    {
        using var g = new GerenciadorDeSessoes(new FakeTimeProvider());
        var token = g.Atual.Token;

        // Consultas não reivindicam.
        Assert.Equal(Acesso.Permitido, g.Autorizar(token, OutroCelular, reivindicar: false, out _));
        Assert.Null(g.Atual.Aparelho);

        Assert.Equal(Acesso.Permitido, g.Autorizar(token, Celular, reivindicar: true, out _));
        Assert.Equal(Celular, g.Atual.Aparelho);
        Assert.Equal(Acesso.Permitido, g.Autorizar(token, IPAddress.Parse("::ffff:192.168.0.20"), true, out _));
        Assert.Equal(Acesso.OutroAparelho, g.Autorizar(token, OutroCelular, true, out _));
        Assert.Equal(Acesso.OutroAparelho, g.Autorizar(token, OutroCelular, false, out _));
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
