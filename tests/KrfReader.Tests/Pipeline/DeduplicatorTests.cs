using KrfReader.Application.Pipeline;

namespace KrfReader.Tests.Pipeline;

public class DeduplicatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    [Fact]
    public void Primeira_leitura_e_aceita()
    {
        var d = new Deduplicator(Window);
        Assert.True(d.ShouldAccept("AA", 1, T0));
    }

    [Fact]
    public void Mesma_tag_mesma_geracao_dentro_da_janela_e_ignorada()
    {
        var d = new Deduplicator(Window);

        Assert.True(d.ShouldAccept("AA", 1, T0));
        Assert.False(d.ShouldAccept("AA", 1, T0));
        Assert.False(d.ShouldAccept("AA", 1, T0.AddSeconds(1)));
        Assert.False(d.ShouldAccept("AA", 1, T0.AddSeconds(4.9)));
    }

    [Fact]
    public void Mesma_tag_apos_a_janela_e_aceita()
    {
        var d = new Deduplicator(Window);

        Assert.True(d.ShouldAccept("AA", 1, T0));
        Assert.True(d.ShouldAccept("AA", 1, T0 + Window + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void Janela_conta_a_partir_da_ultima_leitura_aceita()
    {
        var d = new Deduplicator(Window);

        Assert.True(d.ShouldAccept("AA", 1, T0));
        // Rejeitada: não renova a janela.
        Assert.False(d.ShouldAccept("AA", 1, T0.AddSeconds(4)));
        Assert.True(d.ShouldAccept("AA", 1, T0.AddSeconds(5.5)));
        // Aceita: renova a janela.
        Assert.False(d.ShouldAccept("AA", 1, T0.AddSeconds(10)));
        Assert.True(d.ShouldAccept("AA", 1, T0.AddSeconds(10.6)));
    }

    [Fact]
    public void Geracao_diferente_e_aceita_mesmo_dentro_da_janela()
    {
        var d = new Deduplicator(Window);

        // ENTRADA → SAÍDA → ENTRADA em menos de 1 s: cada troca de modo é uma nova sessão.
        Assert.True(d.ShouldAccept("AA", 1, T0));
        Assert.True(d.ShouldAccept("AA", 2, T0.AddMilliseconds(200)));
        Assert.True(d.ShouldAccept("AA", 3, T0.AddMilliseconds(400)));

        // Dentro da nova geração volta a deduplicar.
        Assert.False(d.ShouldAccept("AA", 3, T0.AddMilliseconds(600)));
    }

    [Fact]
    public void Epcs_diferentes_sao_independentes()
    {
        var d = new Deduplicator(Window);

        Assert.True(d.ShouldAccept("AA", 1, T0));
        Assert.True(d.ShouldAccept("BB", 1, T0));
        Assert.True(d.ShouldAccept("CC", 1, T0.AddMilliseconds(1)));
        Assert.False(d.ShouldAccept("AA", 1, T0.AddSeconds(1)));
        Assert.False(d.ShouldAccept("BB", 1, T0.AddSeconds(1)));
        Assert.Equal(3, d.Count);
    }

    [Fact]
    public void Purge_remove_apenas_expirados()
    {
        var d = new Deduplicator(Window);
        d.ShouldAccept("AA", 1, T0);
        d.ShouldAccept("BB", 1, T0.AddSeconds(1));
        d.ShouldAccept("CC", 1, T0.AddSeconds(4));
        Assert.Equal(3, d.Count);

        Assert.Equal(0, d.Purge(T0.AddSeconds(1)));
        Assert.Equal(3, d.Count);

        var removed = d.Purge(T0.AddSeconds(7));

        Assert.Equal(2, removed);
        Assert.Equal(1, d.Count);
        // CC continua dentro da janela.
        Assert.False(d.ShouldAccept("CC", 1, T0.AddSeconds(7)));
        // AA foi removida e passa a ser aceita.
        Assert.True(d.ShouldAccept("AA", 1, T0.AddSeconds(7)));
    }

    [Fact]
    public void Purge_tudo_expirado_zera_Count()
    {
        var d = new Deduplicator(Window);
        for (var i = 0; i < 100; i++)
            d.ShouldAccept($"E{i:X4}", 1, T0.AddMilliseconds(i));

        Assert.Equal(100, d.Count);
        Assert.Equal(100, d.Purge(T0.AddMinutes(1)));
        Assert.Equal(0, d.Count);
        Assert.Equal(0, d.Purge(T0.AddMinutes(2)));
    }
}
