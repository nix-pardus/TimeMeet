using TimeMeet.Infrastructure.Export;

namespace TimeMeet.UnitTests;

public sealed class QrCodeGeneratorTests
{
    [Fact]
    public void Generate_ReturnsPngForMeetingLink()
    {
        var generator = new QrCodeGenerator();

        var result = generator.Generate("https://timemeet.ru/m/abc123");

        Assert.NotEmpty(result);
        Assert.Equal(0x89, result[0]);
        Assert.Equal((byte)'P', result[1]);
        Assert.Equal((byte)'N', result[2]);
        Assert.Equal((byte)'G', result[3]);
    }

    [Fact]
    public void Generate_RejectsEmptyContent()
    {
        var generator = new QrCodeGenerator();

        Assert.Throws<ArgumentException>(() => generator.Generate(" "));
    }
}