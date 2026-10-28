using QRCoder;

namespace SltDropLocal.Core.Qr;

public static class GeradorDeQr
{
    /// <summary>
    /// PNG do QR Code. Por padrão, correção de erro média (15%): o link é curto, então o
    /// código continua com módulos grandes e fácil de ler mesmo em câmeras ruins.
    /// <paramref name="robusto"/> usa o nível Q (25%), para o QR que o celular lê numa
    /// foto da tela, sujeita a reflexo e moiré.
    /// </summary>
    public static byte[] Png(string texto, int pixelsPorModulo = 12, bool robusto = false)
    {
        using var gerador = new QRCodeGenerator();
        using var dados = gerador.CreateQrCode(texto, robusto ? QRCodeGenerator.ECCLevel.Q : QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(dados).GetGraphic(pixelsPorModulo);
    }
}
