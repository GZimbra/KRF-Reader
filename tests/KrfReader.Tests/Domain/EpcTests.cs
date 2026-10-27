using KrfReader.Domain;

namespace KrfReader.Tests.Domain;

public class EpcTests
{
    [Theory]
    [InlineData("e28011606000abcd", "E28011606000ABCD")]
    [InlineData("E280 1160 6000 ABCD", "E28011606000ABCD")]
    [InlineData("E2-80-11-60", "E2801160")]
    [InlineData("e2:80:11:60", "E2801160")]
    [InlineData("  e2\t80 ", "E280")]
    [InlineData("0123456789abcdefABCDEF", "0123456789ABCDEFABCDEF")]
    public void Normalize_produz_hex_maiusculo_sem_separadores(string raw, string expected)
    {
        Assert.Equal(expected, Epc.Normalize(raw));
        Assert.True(Epc.IsValid(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--")]
    [InlineData(" - : ")]
    [InlineData("E28G")]
    [InlineData("XYZ")]
    [InlineData("E280_1160")]
    [InlineData("E280.1160")]
    public void Normalize_retorna_null_para_invalidos(string? raw)
    {
        Assert.Null(Epc.Normalize(raw));
        Assert.False(Epc.IsValid(raw));
    }

    [Fact]
    public void Normalize_suporta_epc_longo()
    {
        var raw = string.Concat(Enumerable.Repeat("ab", 200));
        Assert.Equal(raw.ToUpperInvariant(), Epc.Normalize(raw));
    }

    [Fact]
    public void Normalize_e_idempotente()
    {
        var once = Epc.Normalize("e2-80 11");
        Assert.Equal(once, Epc.Normalize(once));
    }
}
