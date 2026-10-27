using KrfReader.Infrastructure.Readers;
using KrfReader.Infrastructure.Readers.Protocols;

namespace KrfReader.Tests.Readers;

/// <summary>Quadros <c>A0</c> do Impinj R2000 (conforme a especificação do protocolo serial; sem hardware real).</summary>
public class R2000ProtocolTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 11, 0, 0, DateTimeKind.Utc);
    private const string Epc = "E2801160600002081234ABCD";

    /// <summary>Resposta de tag do 0x89: FreqAnt | PC | EPC | RSSI.</summary>
    private static byte[] TagFrame(byte freqAnt = 0x01, byte rssi = 0x50, string epc = Epc)
    {
        byte[] data = [freqAnt, 0x30, 0x00, .. Convert.FromHexString(epc), rssi];
        return WithCheck([R2000Protocol.Head, (byte)(3 + data.Length), 0xFF, 0x89, .. data]);
    }

    private static byte[] WithCheck(byte[] withoutCheck) => [.. withoutCheck, R2000Protocol.Check(withoutCheck)];

    [Fact]
    public void Checksum_bate_com_exemplo_do_manual()
    {
        // GetFirmwareVersion: A0 03 01 72 EA.
        Assert.Equal(0xEA, R2000Protocol.Check([0xA0, 0x03, 0x01, 0x72]));
    }

    [Fact]
    public void Registrado_na_fabrica()
    {
        Assert.IsType<R2000Protocol>(new ReaderProtocolFactory().Create("R2000"));
    }

    [Fact]
    public void Resposta_de_tag_gera_leitura_com_antena_e_rssi()
    {
        var r = Assert.Single(new R2000Protocol().Feed(TagFrame(freqAnt: 0x0A, rssi: 0x5A), Now));

        Assert.Equal(Epc, r.Epc);
        Assert.Equal(3, r.AntennaId);       // 0x0A & 0x03 = 2 → antena 3
        Assert.Equal(0x5A - 129, r.Rssi);   // −39 dBm
        Assert.Equal(Now, r.TimestampUtc);
    }

    [Fact]
    public void Fim_de_rodada_e_erro_sao_ignorados()
    {
        var p = new R2000Protocol();
        var summary = WithCheck([0xA0, 0x0A, 0xFF, 0x89, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x05]);
        var error = WithCheck([0xA0, 0x04, 0xFF, 0x89, 0x22]);

        Assert.Empty(p.Feed([.. summary, .. error], Now));
        Assert.Equal(0, p.InvalidFrames);
    }

    [Fact]
    public void Quadro_partido_lixo_e_checksum_errado()
    {
        var p = new R2000Protocol();
        var frame = TagFrame();
        var bad = TagFrame();
        bad[^1] ^= 0x01;

        Assert.Empty(p.Feed([0x13, .. bad, .. frame.AsSpan(0, 7)], Now));
        Assert.Equal(1, p.InvalidFrames);
        Assert.Single(p.Feed(frame.AsSpan(7), Now));
    }

    [Fact]
    public void Configuracao_define_antena_e_potencia_limitada()
    {
        var commands = new R2000Protocol().BuildConfigureCommands(40, [3, 2]);

        Assert.Equal(Convert.ToHexString(WithCheck([0xA0, 0x04, 0xFF, 0x74, 0x01])), Convert.ToHexString(commands[0]));
        Assert.Equal(Convert.ToHexString(WithCheck([0xA0, 0x04, 0xFF, 0x76, 33])), Convert.ToHexString(commands[1]));
    }

    [Fact]
    public void Inicio_e_inventario_em_tempo_real_e_parada_e_vazia()
    {
        var p = new R2000Protocol();

        Assert.Equal(0x89, p.BuildStartInventoryCommand()[3]);
        Assert.Empty(p.BuildStopInventoryCommand());
        Assert.True(p.InventoryRefreshInterval > TimeSpan.Zero);
    }
}
