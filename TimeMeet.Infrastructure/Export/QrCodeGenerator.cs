using QRCoder;
using TimeMeet.Application.Meetings;

namespace TimeMeet.Infrastructure.Export;

public sealed class QrCodeGenerator : IMeetingQrCodeGenerator
{
    public byte[] Generate(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Содержимое QR-кода не может быть пустым.", nameof(content));
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var qrCode = new PngByteQRCode(data);
        return qrCode.GetGraphic(10);
    }
}
