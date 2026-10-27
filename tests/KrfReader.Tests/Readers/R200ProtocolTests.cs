using KrfReader.Domain;
using KrfReader.Infrastructure.Readers;
using KrfReader.Infrastructure.Readers.Protocols;

namespace KrfReader.Tests.Readers;

/// <summary>Quadros <c>AA … DD</c> do R200/M100. Respostas capturadas de um módulo real ("M100 26dBm V1.0").</summary>
public class R200ProtocolTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 11, 0, 0, DateTimeKind.Utc);

    // Notificação de tag do manual M100 (cabeçalho/terminador trocados para AA/DD): RSSI C9, PC 3400, EPC 12 bytes.
    private static readonly byte[] TagNotice = Convert.FromHexString("AA02220011C9340030751FEB705C5904E3D50D703A76EFDD");
    private const string TagEpc = "30751FEB705C5904E3D50D70";

    private static readonly byte[] NoTagError = Convert.FromHexString("AA01FF00011516DD");
    private static readonly byte[] HardwareVersion = Convert.FromHexString("AA01030010004D31303020323664426D2056312E3092DD");

    [Fact]
    public void Registrado_na_fabrica()
    {
        Assert.IsType<R200Protocol>(new ReaderProtocolFactory().Create("R200"));
    }

    [Fact]
    public void Notificacao_de_tag_gera_leitura()
    {
        var r = Assert.Single(new R200Protocol().Feed(TagNotice, Now));

        Assert.Equal(TagEpc, r.Epc);
        Assert.Equal(-55, r.Rssi);
        Assert.Equal(R200Protocol.AntennaId, r.AntennaId);
        Assert.Equal(Now, r.TimestampUtc);
    }

    [Fact]
    public void Respostas_e_erros_sao_ignorados_sem_contar_como_invalidos()
    {
        var p = new R200Protocol();

        Assert.Empty(p.Feed([.. NoTagError, .. HardwareVersion], Now));
        Assert.Equal(0, p.InvalidFrames);
    }

    [Fact]
    public void Quadro_partido_e_remontado()
    {
        var p = new R200Protocol();

        Assert.Empty(p.Feed(TagNotice.AsSpan(0, 4), Now));
        Assert.Empty(p.Feed(TagNotice.AsSpan(4, 10), Now));
        Assert.Single(p.Feed(TagNotice.AsSpan(14), Now));
    }

    [Fact]
    public void Varios_quadros_e_lixo_no_mesmo_Feed()
    {
        var p = new R200Protocol();
        byte[] data = [0x00, 0x13, .. NoTagError, .. TagNotice, 0xFF, .. TagNotice];

        Assert.Equal(2, p.Feed(data, Now).Count);
    }

    [Fact]
    public void Checksum_errado_descarta_quadro()
    {
        var corrupted = (byte[])TagNotice.Clone();
        corrupted[^2] ^= 0xFF;
        var p = new R200Protocol();

        Assert.Empty(p.Feed(corrupted, Now));
        Assert.Equal(1, p.InvalidFrames);
        Assert.Single(p.Feed(TagNotice, Now));
    }

    [Fact]
    public void Reset_descarta_parcial()
    {
        var p = new R200Protocol();
        p.Feed(TagNotice.AsSpan(0, 10), Now);
        p.Reset();

        Assert.Single(p.Feed(TagNotice, Now));
    }

    [Theory]
    [InlineData(10, "AA00B6000205DC99DD")] // abaixo do mínimo do módulo → 15 dBm
    [InlineData(20, "AA00B6000207D08FDD")]
    [InlineData(30, "AA00B600020A28EADD")] // acima do máximo → 26 dBm
    public void Configuracao_para_inventario_e_aplica_potencia_limitada(double dbm, string expectedSetPower)
    {
        var commands = new R200Protocol().BuildConfigureCommands(dbm, [1, 2]);

        Assert.Equal(2, commands.Count);
        Assert.Equal("AA0028000028DD", Convert.ToHexString(commands[0]));
        Assert.Equal(expectedSetPower, Convert.ToHexString(commands[1]));
    }

    [Fact]
    public void Comandos_de_inicio_e_parada()
    {
        var p = new R200Protocol();

        Assert.Equal("AA0027000322FFFF4ADD", Convert.ToHexString(p.BuildStartInventoryCommand()));
        Assert.Equal("AA0028000028DD", Convert.ToHexString(p.BuildStopInventoryCommand()));
    }

    [Fact]
    public void Pede_rearme_periodico()
    {
        Assert.True(new R200Protocol().InventoryRefreshInterval > TimeSpan.Zero);
    }

    [Fact]
    public void Leitura_normalizada_igual_a_Epc_Normalize()
    {
        var r = Assert.Single(new R200Protocol().Feed(TagNotice, Now));
        Assert.Equal(Epc.Normalize(r.Epc), r.Epc);
    }
}
