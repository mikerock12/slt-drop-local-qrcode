using System.Text;

namespace SltDropLocal.Core.Envios;

/// <summary>
/// Transforma o nome que o celular mandou num nome seguro para o Windows e escolhe
/// um nome livre na pasta de destino, sem nunca sobrescrever o que já existe.
/// </summary>
public static class NomesDeArquivo
{
    private const int TamanhoMaximo = 180;

    private static readonly HashSet<string> Reservados = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Higienizar(string? nome)
    {
        // Só o último trecho: "../../Windows/x.dll" vira "x.dll".
        var bruto = (nome ?? "").Replace('\\', '/');
        bruto = bruto[(bruto.LastIndexOf('/') + 1)..];

        var invalidos = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(bruto.Length);
        foreach (var c in bruto.Normalize(NormalizationForm.FormC))
            sb.Append(char.IsControl(c) || Array.IndexOf(invalidos, c) >= 0 ? '_' : c);

        // O Windows ignora ponto e espaço no fim do nome; deixá-los causa nomes fantasmas.
        var limpo = sb.ToString().Trim().TrimEnd('.', ' ');
        if (limpo.Length == 0 || limpo.All(c => c == '.')) limpo = "arquivo";

        var extensao = Path.GetExtension(limpo);
        var baseNome = Path.GetFileNameWithoutExtension(limpo);
        if (baseNome.Length == 0) { baseNome = extensao; extensao = ""; } // ".bashrc"
        if (Reservados.Contains(baseNome.Split('.')[0])) baseNome = "_" + baseNome;

        if (extensao.Length > 20) { baseNome += extensao; extensao = ""; }
        if (baseNome.Length + extensao.Length > TamanhoMaximo)
            baseNome = CortarSemPartirSurrogate(baseNome, TamanhoMaximo - extensao.Length);

        return baseNome + extensao;
    }

    /// <summary>"foto.jpg" → "foto (2).jpg" → "foto (3).jpg"... até achar um nome livre.</summary>
    public static string CaminhoLivre(string pasta, string nomeSeguro)
    {
        var caminho = Path.Combine(pasta, nomeSeguro);
        if (!File.Exists(caminho) && !Directory.Exists(caminho)) return caminho;

        var baseNome = Path.GetFileNameWithoutExtension(nomeSeguro);
        var extensao = Path.GetExtension(nomeSeguro);
        for (var i = 2; ; i++)
        {
            caminho = Path.Combine(pasta, $"{baseNome} ({i}){extensao}");
            if (!File.Exists(caminho) && !Directory.Exists(caminho)) return caminho;
        }
    }

    private static string CortarSemPartirSurrogate(string texto, int tamanho)
    {
        if (texto.Length <= tamanho) return texto;
        if (char.IsHighSurrogate(texto[tamanho - 1])) tamanho--;
        return texto[..tamanho];
    }
}
