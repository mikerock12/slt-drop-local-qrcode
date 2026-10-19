using QRCoder;

namespace SltDropLocal.Core.Qr;

public static class GeradorDeQr
{
    /// <summary>
    /// PNG do QR Code. Correção de erro média (15%): o link é curto, então o código
    /// continua com módulos grandes e fácil de ler mesmo em câmeras ruins.
    /// </summary>
    public static byte[] Png(string texto, int pixelsPorModulo = 12)
    {
        using var gerador = new QRCodeGenerator();
        using var dados = gerador.CreateQrCode(texto, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(dados).GetGraphic(pixelsPorModulo);
    }
}
