using KrfReader.Application.Pipeline;
using KrfReader.Domain;

namespace KrfReader.Tests.Pipeline;

public class ReadFilterTests
{
    private static TagRead Read(double rssi, int antenna = 1) =>
        new("E280AA", rssi, antenna, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    [Theory]
    [InlineData(-70.0, true)]
    [InlineData(-69.9, true)]
    [InlineData(-30.0, true)]
    [InlineData(-70.1, false)]
    [InlineData(-90.0, false)]
    public void Rssi_igual_ou_acima_do_minimo_e_aceito(double rssi, bool expected)
    {
        var f = new ReadFilter(-70, []);
        Assert.Equal(expected, f.Accept(Read(rssi)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(99)]
    public void Lista_de_antenas_vazia_aceita_todas(int antenna)
    {
        var f = new ReadFilter(-70, Array.Empty<int>());
        Assert.True(f.Accept(Read(-50, antenna)));
    }

    [Fact]
    public void Antena_fora_da_lista_e_rejeitada()
    {
        var f = new ReadFilter(-70, [1, 3]);

        Assert.True(f.Accept(Read(-50, 1)));
        Assert.True(f.Accept(Read(-50, 3)));
        Assert.False(f.Accept(Read(-50, 2)));
        Assert.False(f.Accept(Read(-50, 4)));
    }

    [Fact]
    public void Rssi_e_antena_precisam_passar_juntos()
    {
        var f = new ReadFilter(-60, [2]);

        Assert.False(f.Accept(Read(-61, 2)));
        Assert.False(f.Accept(Read(-40, 1)));
        Assert.True(f.Accept(Read(-60, 2)));
    }

    [Fact]
    public void Update_altera_configuracao_em_runtime()
    {
        var f = new ReadFilter(-50, [1]);
        Assert.False(f.Accept(Read(-60, 1)));
        Assert.False(f.Accept(Read(-40, 2)));

        f.Update(-65, [2]);

        Assert.True(f.Accept(Read(-60, 2)));
        Assert.False(f.Accept(Read(-60, 1)));
        Assert.False(f.Accept(Read(-66, 2)));

        f.Update(-65, []);
        Assert.True(f.Accept(Read(-60, 1)));
        Assert.True(f.Accept(Read(-60, 7)));
    }

    [Fact]
    public void Update_nao_e_afetado_por_mutacao_posterior_da_colecao_original()
    {
        var antennas = new List<int> { 1 };
        var f = new ReadFilter(-70, antennas);

        antennas.Add(2);

        Assert.False(f.Accept(Read(-50, 2)));
    }

    [Fact]
    public async Task Accept_e_Update_concorrentes_nao_lancam()
    {
        var f = new ReadFilter(-70, [1]);

        // Invariantes válidas em qualquer configuração que o writer aplica:
        // minRssi ∈ [-70, -61] e antena 1 sempre permitida, antena 3 nunca.
        var reader = Task.Run(() =>
        {
            var violations = 0;
            for (var n = 0; n < 100_000; n++)
            {
                if (!f.Accept(Read(-60, 1))) violations++;
                if (f.Accept(Read(-60, 3))) violations++;
                f.Accept(Read(-60, 2));
            }
            return violations;
        });
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 50_000; i++)
                f.Update(-70 + (i % 10), i % 2 == 0 ? [1] : [1, 2]);
        });

        await Task.WhenAll(reader, writer).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, await reader);
    }
}
