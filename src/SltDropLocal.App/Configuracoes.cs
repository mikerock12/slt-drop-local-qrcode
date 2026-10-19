using System.Text.Json;

namespace SltDropLocal.App;

/// <summary>Lembra a última pasta e a última placa de rede escolhidas. Fica em %APPDATA%.</summary>
internal sealed class Configuracoes
{
    private static readonly string Arquivo = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Program.NomeApp, "config.json");

    public string? UltimaPasta { get; set; }
    public string? UltimoIp { get; set; }

    public static Configuracoes Carregar()
    {
        try
        {
            return File.Exists(Arquivo)
                ? JsonSerializer.Deserialize<Configuracoes>(File.ReadAllText(Arquivo)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Salvar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Arquivo)!);
            File.WriteAllText(Arquivo, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Não lembrar a pasta da próxima vez não impede de usar o programa agora.
        }
    }
}
