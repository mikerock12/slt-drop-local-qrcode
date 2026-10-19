using SltDropLocal.Core.Envios;

namespace SltDropLocal.Tests;

public class NomesDeArquivoTests
{
    [Theory]
    [InlineData("foto.jpg", "foto.jpg")]
    [InlineData("../../Windows/System32/evil.dll", "evil.dll")]
    [InlineData(@"..\..\evil.bat", "evil.bat")]
    [InlineData("a<b>c:d\"e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("relatório final.pdf", "relatório final.pdf")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("com1.tar.gz", "_com1.tar.gz")]
    [InlineData("nome com ponto no fim...", "nome com ponto no fim")]
    [InlineData("   ", "arquivo")]
    [InlineData("", "arquivo")]
    [InlineData(null, "arquivo")]
    [InlineData("...", "arquivo")]
    [InlineData(".bashrc", ".bashrc")]
    [InlineData("vídeo 😀.mp4", "vídeo 😀.mp4")]
    public void Higienizar(string? entrada, string esperado) =>
        Assert.Equal(esperado, NomesDeArquivo.Higienizar(entrada));

    [Fact]
    public void Nome_muito_longo_eh_cortado_mantendo_a_extensao()
    {
        var nome = NomesDeArquivo.Higienizar(new string('a', 400) + ".mp4");
        Assert.True(nome.Length <= 180);
        Assert.EndsWith(".mp4", nome);
    }

    [Fact]
    public void Caracteres_de_controle_viram_sublinhado()
    {
        Assert.Equal("a_b.txt", NomesDeArquivo.Higienizar("a\u0001b.txt"));
    }

    [Fact]
    public void Caminho_livre_nunca_sobrescreve()
    {
        var pasta = Directory.CreateTempSubdirectory("sltdrop-nomes-").FullName;
        try
        {
            Assert.Equal(Path.Combine(pasta, "foto.jpg"), NomesDeArquivo.CaminhoLivre(pasta, "foto.jpg"));
            File.WriteAllText(Path.Combine(pasta, "foto.jpg"), "1");
            Assert.Equal(Path.Combine(pasta, "foto (2).jpg"), NomesDeArquivo.CaminhoLivre(pasta, "foto.jpg"));
            File.WriteAllText(Path.Combine(pasta, "foto (2).jpg"), "2");
            Assert.Equal(Path.Combine(pasta, "foto (3).jpg"), NomesDeArquivo.CaminhoLivre(pasta, "foto.jpg"));
            Directory.CreateDirectory(Path.Combine(pasta, "pasta"));
            Assert.Equal(Path.Combine(pasta, "pasta (2)"), NomesDeArquivo.CaminhoLivre(pasta, "pasta"));
        }
        finally { Directory.Delete(pasta, true); }
    }
}
