using KrfReader.Application.Pipeline;
using KrfReader.Domain;

namespace KrfReader.Tests.Pipeline;

public class ModeStateTests
{
    [Fact]
    public void Estado_inicial_sem_modo()
    {
        var s = new ModeState();

        Assert.Null(s.Mode);
        Assert.Equal((s.Mode, s.Generation), s.Snapshot());
    }

    [Fact]
    public void Set_incrementa_geracao_e_retorna_a_nova()
    {
        var s = new ModeState();
        var g0 = s.Generation;

        var g1 = s.Set(OperationMode.Entrada);
        Assert.Equal(g0 + 1, g1);
        Assert.Equal(g1, s.Generation);
        Assert.Equal(OperationMode.Entrada, s.Mode);

        var g2 = s.Set(OperationMode.Saida);
        Assert.Equal(g1 + 1, g2);
        Assert.Equal(OperationMode.Saida, s.Mode);

        var g3 = s.Set(null);
        Assert.Equal(g2 + 1, g3);
        Assert.Null(s.Mode);
    }

    [Fact]
    public void Set_com_mesmo_modo_tambem_incrementa()
    {
        var s = new ModeState();
        var g1 = s.Set(OperationMode.Entrada);
        var g2 = s.Set(OperationMode.Entrada);
        Assert.Equal(g1 + 1, g2);
    }

    [Fact]
    public void Snapshot_reflete_ultimo_Set()
    {
        var s = new ModeState();
        var g = s.Set(OperationMode.Saida);

        var (mode, generation) = s.Snapshot();

        Assert.Equal(OperationMode.Saida, mode);
        Assert.Equal(g, generation);
    }

    [Fact]
    public async Task Snapshot_e_consistente_sob_concorrencia()
    {
        var s = new ModeState();
        // Convenção do teste: geração par ⇔ Entrada, ímpar ⇔ Saida (verificada no snapshot).
        var start = s.Generation;
        const int sets = 20_000;
        var violations = 0;
        var done = false;

        var writer = Task.Run(() =>
        {
            for (var i = 0; i < sets; i++)
            {
                lock (s)
                {
                    var next = s.Generation + 1;
                    s.Set(((next - start) % 2 == 0) ? OperationMode.Entrada : OperationMode.Saida);
                }
            }
            Volatile.Write(ref done, true);
        });

        var reader = Task.Run(() =>
        {
            while (!Volatile.Read(ref done))
            {
                var (mode, gen) = s.Snapshot();
                if (gen == start)
                    continue;
                var expected = ((gen - start) % 2 == 0) ? OperationMode.Entrada : OperationMode.Saida;
                if (mode != expected)
                    Interlocked.Increment(ref violations);
            }
        });

        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, violations);
        Assert.Equal(start + sets, s.Generation);
    }

    [Fact]
    public async Task Set_concorrente_gera_geracoes_unicas()
    {
        var s = new ModeState();
        var start = s.Generation;
        const int perTask = 2_000;
        const int tasks = 8;

        var results = await Task.WhenAll(Enumerable.Range(0, tasks).Select(t => Task.Run(() =>
        {
            var list = new long[perTask];
            for (var i = 0; i < perTask; i++)
                list[i] = s.Set(t % 2 == 0 ? OperationMode.Entrada : OperationMode.Saida);
            return list;
        }))).WaitAsync(TimeSpan.FromSeconds(10));

        var all = results.SelectMany(x => x).ToArray();
        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Equal(start + tasks * perTask, s.Generation);
    }
}
