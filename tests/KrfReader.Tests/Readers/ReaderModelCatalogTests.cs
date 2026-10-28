using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Infrastructure.Readers;

namespace KrfReader.Tests.Readers;

public class ReaderModelCatalogTests
{
    private static readonly ReaderModelCatalog Catalog = ReaderModelCatalog.Default;

    [Fact]
    public void Todo_modelo_serial_ou_tcp_usa_protocolo_registrado()
    {
        var factory = new ReaderProtocolFactory();
        foreach (var m in Catalog.Models.Where(m => m.Type is ReaderType.Serial or ReaderType.Tcp))
            Assert.Contains(m.Protocol, factory.AvailableProtocols, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Todo_modelo_e_criado_pela_fabrica_sem_conectar()
    {
        var factory = new RfidReaderFactory(new ReaderProtocolFactory());
        foreach (var m in Catalog.Models.Where(m => !m.IsCustom))
        {
            await using var reader = factory.Create(new ReaderOptions
            {
                Model = m.Id,
                Type = m.Type,
                Protocol = m.Protocol,
                Tcp = new TcpReaderOptions { Host = "192.0.2.1", Port = m.DefaultTcpPort },
            });
            Assert.False(reader.IsConnected, m.Id);
        }
    }

    [Theory]
    [InlineData(ReaderModelCatalog.ImpinjR700LlrpId, ReaderType.Llrp, 5084)]
    [InlineData(ReaderModelCatalog.ZebraFx9600Id, ReaderType.Llrp, 5084)]
    [InlineData(ReaderModelCatalog.ImpinjR700IotId, ReaderType.ImpinjIot, 443)]
    [InlineData(ReaderModelCatalog.ChafonRu5106Id, ReaderType.Tcp, 4001)]
    public void Leitores_industriais_no_catalogo(string id, ReaderType type, int port)
    {
        var m = Catalog.Find(id);
        Assert.NotNull(m);
        Assert.Equal(type, m!.Type);
        Assert.Equal(port, m.DefaultTcpPort);
        Assert.True(m.AntennaPorts >= 4);
        Assert.False(m.Validated);
    }

    [Fact]
    public void Faixa_de_potencia_coerente()
    {
        foreach (var m in Catalog.Models)
        {
            Assert.True(m.MinPowerDbm <= m.DefaultPowerDbm && m.DefaultPowerDbm <= m.MaxPowerDbm, m.Id);
            Assert.True(m.AntennaPorts >= 1, m.Id);
        }
    }

    [Fact]
    public void Find_e_case_insensitive()
    {
        Assert.Equal(ReaderModelCatalog.R200Id, Catalog.Find("R200")?.Id);
        Assert.Null(Catalog.Find("inexistente"));
        Assert.Null(Catalog.Find(null));
    }

    [Fact]
    public void Resolve_usa_Model_quando_existe()
    {
        var m = Catalog.Resolve(new ReaderOptions { Model = ReaderModelCatalog.R2000TcpId, Type = ReaderType.Serial });
        Assert.Equal(ReaderModelCatalog.R2000TcpId, m.Id);
    }

    [Theory]
    [InlineData(ReaderType.Serial, "R200", ReaderModelCatalog.R200Id)]
    [InlineData(ReaderType.Tcp, "r2000", ReaderModelCatalog.R2000TcpId)]
    [InlineData(ReaderType.Serial, "AsciiLine", ReaderModelCatalog.AsciiSerialId)]
    [InlineData(ReaderType.Tcp, "Desconhecido", ReaderModelCatalog.CustomId)]
    [InlineData(ReaderType.ImpinjIot, "ImpinjIoT", ReaderModelCatalog.ImpinjR700IotId)]
    public void Resolve_deduz_por_tipo_e_protocolo(ReaderType type, string protocol, string expected)
    {
        Assert.Equal(expected, Catalog.Resolve(new ReaderOptions { Type = type, Protocol = protocol }).Id);
    }

    [Fact]
    public void Ids_duplicados_sao_rejeitados()
    {
        var a = new ReaderModel { Id = "x", DisplayName = "x", Family = "x", Type = ReaderType.Serial, IsCustom = true };
        Assert.Throws<ArgumentException>(() => new ReaderModelCatalog([a, a with { DisplayName = "y" }]));
    }
}
