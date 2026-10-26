using System.Text;
using KrfReader.Domain;
using KrfReader.Infrastructure.Readers.Protocols;

namespace KrfReader.Tests.Readers;

/// <summary>Formato: <c>EPC,RSSI,ANTENA[,TIMESTAMP_ISO8601]\n</c>.</summary>
public class AsciiLineProtocolTests
{
    private static readonly DateTime Now = new(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<TagRead> Feed(AsciiLineProtocol p, string text) =>
        p.Feed(Encoding.ASCII.GetBytes(text), Now);

    [Fact]
    public void Nome_registrado()
    {
        Assert.Equal("AsciiLine", new AsciiLineProtocol().Name);
    }

    [Fact]
    public void Linha_completa_gera_leitura_normalizada()
    {
        var reads = Feed(new AsciiLineProtocol(), "e2801160aabb,-52.5,2\n");

        var r = Assert.Single(reads);
        Assert.Equal("E2801160AABB", r.Epc);
        Assert.Equal(-52.5, r.Rssi);
        Assert.Equal(2, r.AntennaId);
        Assert.Equal(Now, r.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, r.TimestampUtc.Kind);
    }

    [Fact]
    public void Linha_partida_em_dois_Feed_e_remontada()
    {
        var p = new AsciiLineProtocol();

        Assert.Empty(Feed(p, "E28011"));
        Assert.Empty(Feed(p, "60AABB,-4"));
        var r = Assert.Single(Feed(p, "0,1\n"));

        Assert.Equal("E2801160AABB", r.Epc);
        Assert.Equal(-40, r.Rssi);
        Assert.Equal(1, r.AntennaId);
    }

    [Fact]
    public void Varias_linhas_em_um_Feed_e_crlf()
    {
        var reads = Feed(new AsciiLineProtocol(), "AA01,-40,1\r\nAA02,-41,2\r\nAA03,-42,3\n");

        Assert.Equal(["AA01", "AA02", "AA03"], reads.Select(r => r.Epc));
        Assert.Equal([1, 2, 3], reads.Select(r => r.AntennaId));
    }

    [Fact]
    public void Crlf_dividido_entre_Feed()
    {
        var p = new AsciiLineProtocol();
        Assert.Empty(Feed(p, "AA01,-40,1\r"));
        var r = Assert.Single(Feed(p, "\n"));
        Assert.Equal(1, r.AntennaId);
    }

    [Fact]
    public void Comentario_e_linha_vazia_sao_ignorados_sem_contar_invalida()
    {
        var p = new AsciiLineProtocol();

        var reads = Feed(p, "# cabeçalho\n\n   \r\nAA01,-40,1\n");

        Assert.Single(reads);
        Assert.Equal(0, p.InvalidLines);
    }

    [Theory]
    [InlineData("ZZZZ,-40,1")]
    [InlineData("AA01,abc,1")]
    [InlineData("AA01,-40,x")]
    [InlineData("AA01,-40")]
    [InlineData("AA01")]
    [InlineData(",-40,1")]
    [InlineData("AA01,-40,1,2026-99-99")]
    [InlineData("AA01,-40,1,x,y")]
    public void Linha_invalida_e_descartada_e_a_seguinte_continua_valendo(string bad)
    {
        var p = new AsciiLineProtocol();

        var reads = Feed(p, bad + "\nBB02,-45,2\n");

        var r = Assert.Single(reads);
        Assert.Equal("BB02", r.Epc);
        Assert.Equal(1, p.InvalidLines);
    }

    [Fact]
    public void Timestamp_explicito_e_usado_em_utc()
    {
        var reads = Feed(new AsciiLineProtocol(),
            "AA01,-40,1,2026-01-02T03:04:05.123Z\nAA02,-40,1,2026-01-02T03:04:05-03:00\n");

        Assert.Equal(2, reads.Count);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, 123, DateTimeKind.Utc), reads[0].TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, reads[0].TimestampUtc.Kind);
        Assert.Equal(new DateTime(2026, 1, 2, 6, 4, 5, DateTimeKind.Utc), reads[1].TimestampUtc);
    }

    [Fact]
    public void Sem_timestamp_usa_instante_de_recepcao()
    {
        var at = new DateTime(2030, 7, 7, 7, 7, 7, DateTimeKind.Utc);
        var r = Assert.Single(new AsciiLineProtocol().Feed("AA01,-40,1\n"u8, at));
        Assert.Equal(at, r.TimestampUtc);
    }

    [Fact]
    public void Linha_acima_de_1024_bytes_sem_terminador_e_descartada()
    {
        var p = new AsciiLineProtocol();
        var garbage = new string('A', AsciiLineProtocol.MaxLineBytes + 500);

        Assert.Empty(Feed(p, garbage));
        Assert.Empty(Feed(p, "AAAA,-40,1\n")); // ainda é o final da linha gigante: descartado inteiro
        var r = Assert.Single(Feed(p, "CC03,-40,1\n"));

        Assert.Equal("CC03", r.Epc);
        Assert.True(p.InvalidLines >= 1);
    }

    [Fact]
    public void Reset_descarta_bytes_parciais()
    {
        var p = new AsciiLineProtocol();
        Feed(p, "DEAD,-4");
        p.Reset();

        var r = Assert.Single(Feed(p, "BEEF,-50,3\n"));
        Assert.Equal("BEEF", r.Epc);
    }

    [Fact]
    public void Comandos_start_stop()
    {
        var p = new AsciiLineProtocol();

        Assert.Equal("START\n", Encoding.ASCII.GetString(p.BuildStartInventoryCommand()));
        Assert.Equal("STOP\n", Encoding.ASCII.GetString(p.BuildStopInventoryCommand()));
    }

    [Fact]
    public void Comandos_de_configuracao_incluem_potencia_com_cultura_invariante()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
        try
        {
            var commands = new AsciiLineProtocol().BuildConfigureCommands(27.5, [1, 2])
                .Select(Encoding.ASCII.GetString)
                .ToArray();

            Assert.Contains("POWER 27.5\n", commands);
            Assert.All(commands, c => Assert.EndsWith("\n", c));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
