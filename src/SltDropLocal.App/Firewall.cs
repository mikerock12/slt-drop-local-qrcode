using System.Diagnostics;

namespace SltDropLocal.App;

/// <summary>
/// O instalador cria a regra de entrada no Firewall do Windows. Aqui só conferimos
/// se ela existe, para avisar na tela quando o programa roda sem ter sido instalado.
/// </summary>
internal static class Firewall
{
    public const string NomeRegra = "SLT Drop Local QRCode";

    public static async Task<bool?> RegraExisteAsync()
    {
        try
        {
            using var netsh = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                Arguments = $"advfirewall firewall show rule name=\"{NomeRegra}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (netsh is null) return null;
            await netsh.StandardOutput.ReadToEndAsync();
            await netsh.WaitForExitAsync();
            return netsh.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
