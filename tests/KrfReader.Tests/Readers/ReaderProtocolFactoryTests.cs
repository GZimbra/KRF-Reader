using KrfReader.Application.Readers;
using KrfReader.Domain;
using KrfReader.Infrastructure.Readers;
using KrfReader.Infrastructure.Readers.Protocols;

namespace KrfReader.Tests.Readers;

public class ReaderProtocolFactoryTests
{
    private sealed class DummyProtocol : IReaderProtocol
    {
        public string Name => "Dummy";
        public IReadOnlyList<TagRead> Feed(ReadOnlySpan<byte> data, DateTime receivedAtUtc) => [];
        public IReadOnlyList<byte[]> BuildConfigureCommands(double powerDbm, IReadOnlyCollection<int> antennas) => [];
        public byte[] BuildStartInventoryCommand() => [];
        public byte[] BuildStopInventoryCommand() => [];
        public void Reset() { }
    }

    [Fact]
    public void Registra_AsciiLine_por_padrao()
    {
        var f = new ReaderProtocolFactory();

        Assert.Contains("AsciiLine", f.AvailableProtocols, StringComparer.OrdinalIgnoreCase);
        Assert.IsType<AsciiLineProtocol>(f.Create("AsciiLine"));
    }

    [Fact]
    public void Create_devolve_instancia_nova_a_cada_chamada()
    {
        var f = new ReaderProtocolFactory();
        Assert.NotSame(f.Create("AsciiLine"), f.Create("AsciiLine"));
    }

    [Theory]
    [InlineData("Inexistente")]
    [InlineData("")]
    public void Nome_desconhecido_lanca(string name)
    {
        var f = new ReaderProtocolFactory();
        Assert.ThrowsAny<ArgumentException>(() => f.Create(name));
    }

    [Fact]
    public void Register_e_Create_sao_case_insensitive()
    {
        var f = new ReaderProtocolFactory();
        f.Register("Dummy", () => new DummyProtocol());

        Assert.IsType<DummyProtocol>(f.Create("dummy"));
        Assert.IsType<DummyProtocol>(f.Create("DUMMY"));
        Assert.IsType<AsciiLineProtocol>(f.Create("asciiline"));
        Assert.Contains("Dummy", f.AvailableProtocols, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_com_mesmo_nome_em_outra_caixa_substitui()
    {
        var f = new ReaderProtocolFactory();
        f.Register("ASCIILINE", () => new DummyProtocol());

        Assert.IsType<DummyProtocol>(f.Create("AsciiLine"));
        Assert.Single(f.AvailableProtocols, n => n.Equals("asciiline", StringComparison.OrdinalIgnoreCase));
    }
}
