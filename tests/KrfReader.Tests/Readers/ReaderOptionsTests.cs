using System.Text;
using KrfReader.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace KrfReader.Tests.Readers;

public class ReaderOptionsTests
{
    [Fact]
    public void Potencia_por_porta_sobrepoe_a_global()
    {
        var o = new ReaderOptions { PowerDbm = 30, Antennas = [3, 1, 3], AntennaPowerDbm = new() { [3] = 25 } };

        Assert.Equal([new(1, 30), new(3, 25)], o.ActivePorts());
    }

    [Fact]
    public void Appsettings_com_secoes_novas_e_lido()
    {
        const string json = """
            {"Reader":{"Type":"Llrp","Antennas":[1,2],"PowerDbm":30,"AntennaPowerDbm":{"2":27.5},"EnforceAnatelBand":false,
              "Tcp":{"Host":"10.0.0.5","Port":5084},"Llrp":{"KeepaliveMs":2000},"ImpinjIot":{"Username":"u","UseHttps":false}}}
            """;
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

        var o = config.GetSection(ReaderOptions.SectionName).Get<ReaderOptions>()!;

        Assert.Equal(ReaderType.Llrp, o.Type);
        Assert.Equal(27.5, o.AntennaPowerDbm[2]);
        Assert.False(o.EnforceAnatelBand);
        Assert.Equal(2000, o.Llrp.KeepaliveMs);
        Assert.Equal("u", o.ImpinjIot.Username);
        Assert.False(o.ImpinjIot.UseHttps);
        Assert.Equal("impinj", o.ImpinjIot.Password);
    }
}
