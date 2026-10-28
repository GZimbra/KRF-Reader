using KrfReader.Application.Readers;
using KrfReader.Infrastructure.Readers.Protocols;

namespace KrfReader.Tests.Readers;

/// <summary>R2000 com 4 portas: 0x8A, potência por porta e região Anatel (conforme o protocolo; sem hardware real).</summary>
public class R2000MultiAntennaTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] Data(byte[] frame) => frame[4..^1];

    [Fact]
    public void Uma_porta_mantem_0x89_e_antena_de_trabalho()
    {
        var p = new R2000Protocol();

        var commands = p.BuildRadioCommands(new RadioSetup([new AntennaPort(3, 26)], EnforceAnatelBand: false));

        Assert.Equal([0x74, 0x76], commands.Select(c => c[3]).ToArray());
        Assert.Equal([2], Data(commands[0]));
        Assert.Equal([26], Data(commands[1]));
        Assert.Equal(0x89, p.BuildStartInventoryCommand()[3]);
    }

    [Fact]
    public void Varias_portas_usam_troca_rapida_0x8A()
    {
        var p = new R2000Protocol();
        p.BuildRadioCommands(new RadioSetup([new AntennaPort(1, 30), new AntennaPort(3, 30)], EnforceAnatelBand: false));

        var start = p.BuildStartInventoryCommand();

        Assert.Equal(0x8A, start[3]);
        // A=0 loop1, B=2 loop1, C/D puladas, intervalo 0, repetição 1.
        Assert.Equal([0x00, 1, 0x02, 1, 0xFF, 1, 0xFF, 1, 0, 1], Data(start));
    }

    [Fact]
    public void Potencias_diferentes_vao_em_0x76_com_quatro_bytes()
    {
        var p = new R2000Protocol();

        var commands = p.BuildRadioCommands(new RadioSetup([new AntennaPort(1, 30), new AntennaPort(2, 25)], EnforceAnatelBand: false));

        var power = commands.Single(c => c[3] == 0x76);
        Assert.Equal([30, 25, 25, 25], Data(power));
        Assert.DoesNotContain(commands, c => c[3] == 0x74);
    }

    [Fact]
    public void Anatel_configura_regiao_FCC_de_915_5_a_927_5()
    {
        var commands = new R2000Protocol().BuildRadioCommands(new RadioSetup([new AntennaPort(1, 30)], EnforceAnatelBand: true));

        var region = commands[0];
        Assert.Equal(0x78, region[3]);
        Assert.Equal([0x01, 0x22, 0x3A], Data(region));
        static double Mhz(byte point) => 902.0 + 0.5 * (point - 7);
        Assert.Equal(915.5, Mhz(0x22));
        Assert.Equal(927.5, Mhz(0x3A));
    }

    [Fact]
    public void Resposta_de_tag_do_0x8A_gera_leitura()
    {
        const string epc = "E2801160600002081234ABCD";
        byte[] data = [0x03, 0x30, 0x00, .. Convert.FromHexString(epc), 0x55];
        byte[] noCheck = [R2000Protocol.Head, (byte)(3 + data.Length), 0xFF, 0x8A, .. data];

        var read = Assert.Single(new R2000Protocol().Feed([.. noCheck, R2000Protocol.Check(noCheck)], Now));

        Assert.Equal(epc, read.Epc);
        Assert.Equal(4, read.AntennaId);
    }
}
