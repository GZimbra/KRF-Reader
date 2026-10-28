using System.Buffers.Binary;
using System.Collections.Concurrent;
using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Domain;
using KrfReader.Infrastructure.Readers;
using KrfReader.Infrastructure.Readers.Llrp;
using KrfReader.Tests.Fakes;

namespace KrfReader.Tests.Readers;

/// <summary>Driver LLRP contra um leitor LLRP falso em loopback (sem hardware real).</summary>
public sealed class LlrpReaderTests : IAsyncLifetime
{
    private const string Epc1 = "E2801160600002081234ABCD";
    private const string Epc2 = "300833B2DDD9014000000001";

    private readonly FakeLlrpServer _server = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private ReaderOptions Options(int[]? antennas = null, Dictionary<int, double>? power = null) => new()
    {
        Type = ReaderType.Llrp,
        Tcp = new TcpReaderOptions { Host = "127.0.0.1", Port = _server.Port, ConnectTimeoutMs = 2000 },
        Antennas = antennas ?? [1, 2],
        PowerDbm = 30,
        AntennaPowerDbm = power ?? [],
        ReconnectDelayMs = 50,
        Llrp = new LlrpReaderOptions { KeepaliveMs = 5000, ResponseTimeoutMs = 2000 },
        Inventory = new InventoryOptions { Session = GenSession.S2, InitialQ = 5, Target = TargetStrategy.DualAB },
    };

    [Fact]
    public async Task Conexao_configura_o_leitor_do_zero()
    {
        await using var reader = new LlrpReader(Options(power: new() { [2] = 27 }));

        await reader.ConnectAsync().WithTimeout();

        Assert.True(reader.IsConnected);
        Assert.Equal(
            [
                LlrpMessageType.GetReaderCapabilities, LlrpMessageType.SetReaderConfig, LlrpMessageType.DeleteRoSpec,
                LlrpMessageType.AddRoSpec, LlrpMessageType.EnableRoSpec,
            ],
            _server.ReceivedTypes());
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(_server.Last(LlrpMessageType.DeleteRoSpec).Body));
        Assert.False(FakeLlrpServer.HasImpinjCustom(_server.Last(LlrpMessageType.AddRoSpec)));
        Assert.NotNull(reader.Info);
        Assert.Contains("Zebra", reader.Info!.Description);
        Assert.Contains("1@30 dBm", reader.Info.Description);
        Assert.Contains("2@27 dBm", reader.Info.Description);
    }

    [Fact]
    public async Task Inventario_entrega_leituras_so_entre_start_e_stop()
    {
        await using var reader = new LlrpReader(Options());
        var reads = new ConcurrentQueue<TagRead>();
        reader.TagRead += (_, e) => reads.Enqueue(e.Read);
        await reader.ConnectAsync().WithTimeout();

        await _server.SendReportAsync(FakeLlrpServer.TagReport(Epc2, 1, -50));
        await Barrier(1);
        await reader.StartInventoryAsync().WithTimeout();
        Assert.Equal(LlrpMessageType.StartRoSpec, _server.ReceivedTypes()[^1]);

        await _server.SendReportAsync(FakeLlrpServer.TagReport(Epc1, 2, -55), FakeLlrpServer.TagReport(Epc2, 1, -60));
        await TestUtil.Eventually(() => reads.Count == 2, "duas leituras do relatório");

        await reader.StopInventoryAsync().WithTimeout();
        Assert.Equal(LlrpMessageType.StopRoSpec, _server.ReceivedTypes()[^1]);
        await _server.SendReportAsync(FakeLlrpServer.TagReport(Epc1, 2, -55));
        await Barrier(2);

        var list = reads.ToArray();
        Assert.Equal(2, list.Length);
        Assert.Equal((Epc1, 2, -55.0), (list[0].Epc, list[0].AntennaId, list[0].Rssi));
        Assert.Equal((Epc2, 1, -60.0), (list[1].Epc, list[1].AntennaId, list[1].Rssi));
    }

    /// <summary>KEEPALIVE → ACK: quando o ACK chega, o leitor já processou tudo o que o fake enviou antes.</summary>
    private async Task Barrier(uint id)
    {
        await _server.SendKeepaliveAsync(id);
        await TestUtil.Eventually(() => _server.Received.Any(m => m.Type == LlrpMessageType.KeepaliveAck && m.Id == id), "barreira KEEPALIVE");
    }

    [Fact]
    public async Task Leitor_em_regiao_FCC_e_recusado()
    {
        _server.HopTables = [new LlrpHopTable(1, Enumerable.Range(0, 50).Select(i => (uint)(902_750 + 500 * i)).ToArray())];
        await using var reader = new LlrpReader(Options());

        var ex = await Assert.ThrowsAsync<LlrpException>(() => reader.ConnectAsync().WithTimeout());

        Assert.Contains("915–928", ex.Message);
        Assert.False(reader.IsConnected);
        Assert.DoesNotContain(LlrpMessageType.AddRoSpec, _server.ReceivedTypes());
    }

    [Fact]
    public async Task Porta_inexistente_e_recusada()
    {
        _server.MaxAntennas = 2;
        await using var reader = new LlrpReader(Options(antennas: [1, 4]));

        var ex = await Assert.ThrowsAsync<LlrpException>(() => reader.ConnectAsync().WithTimeout());

        Assert.Contains("Porta 4", ex.Message);
    }

    [Fact]
    public async Task Outro_cliente_conectado_e_reportado()
    {
        _server.ConnectionStatus = 2;
        await using var reader = new LlrpReader(Options());

        var ex = await Assert.ThrowsAsync<LlrpException>(() => reader.ConnectAsync().WithTimeout());

        Assert.Contains("outro cliente", ex.Message);
    }

    [Fact]
    public async Task Status_de_erro_vira_excecao_com_o_texto_do_leitor()
    {
        _server.FailingTypes.Add(LlrpMessageType.AddRoSpec);
        await using var reader = new LlrpReader(Options());

        var ex = await Assert.ThrowsAsync<LlrpException>(() => reader.ConnectAsync().WithTimeout());

        Assert.Contains("ADD_ROSPEC", ex.Message);
        Assert.Contains("recusado pelo fake", ex.Message);
    }

    [Fact]
    public async Task Queda_reconecta_reconfigura_e_retoma_o_inventario()
    {
        await using var reader = new LlrpReader(Options());
        var reads = new ConcurrentQueue<TagRead>();
        var errors = new ConcurrentQueue<string>();
        reader.TagRead += (_, e) => reads.Enqueue(e.Read);
        reader.Error += (_, e) => errors.Enqueue(e.Message);
        await reader.ConnectAsync().WithTimeout();
        await reader.StartInventoryAsync().WithTimeout();
        _server.Clear();

        _server.DropClient();

        await TestUtil.Eventually(() => _server.Connections == 2 && _server.ReceivedTypes().Contains(LlrpMessageType.StartRoSpec),
            "reconexão com START_ROSPEC");
        await TestUtil.Eventually(() => reader.IsConnected, "link de volta");
        Assert.Equal(LlrpMessageType.AddRoSpec, _server.ReceivedTypes().SkipWhile(t => t != LlrpMessageType.AddRoSpec).First());
        Assert.Contains(errors, e => e.Contains("perdida"));
        Assert.Equal(1, reader.GetDiagnostics().LinkDrops);

        await _server.SendReportAsync(FakeLlrpServer.TagReport(Epc1, 1, -40));
        await TestUtil.Eventually(() => reads.Count == 1, "leitura após reconectar");
    }

    [Fact]
    public async Task Sem_keepalive_o_watchdog_derruba_e_reconecta()
    {
        var options = Options();
        options.Llrp.KeepaliveMs = 100; // fake não envia KEEPALIVE: 300 ms sem mensagem = link morto
        await using var reader = new LlrpReader(options);
        var errors = new ConcurrentQueue<string>();
        reader.Error += (_, e) => errors.Enqueue(e.Message);

        await reader.ConnectAsync().WithTimeout();

        await TestUtil.Eventually(() => _server.Connections >= 2, "reconexão pelo watchdog");
        Assert.Contains(errors, e => e.Contains("KEEPALIVE"));
        Assert.True(reader.GetDiagnostics().LinkDrops >= 1);
    }

    [Fact]
    public async Task Keepalive_e_respondido_com_o_mesmo_id()
    {
        await using var reader = new LlrpReader(Options());
        await reader.ConnectAsync().WithTimeout();

        await _server.SendKeepaliveAsync(4242);

        await TestUtil.Eventually(() => _server.Received.Any(m => m.Type == LlrpMessageType.KeepaliveAck && m.Id == 4242), "KEEPALIVE_ACK");
    }

    [Fact]
    public async Task Impinj_usa_extensao_de_modo_de_busca_e_recai_sem_ela()
    {
        _server.ManufacturerPen = LlrpProtocol.ImpinjPen;
        _server.RejectCustomParameters = true;
        await using var reader = new LlrpReader(Options());

        await reader.ConnectAsync().WithTimeout();

        var adds = _server.Received.Where(m => m.Type == LlrpMessageType.AddRoSpec).ToArray();
        Assert.Contains(LlrpMessageType.CustomMessage, _server.ReceivedTypes());
        Assert.Equal(2, adds.Length);
        Assert.True(FakeLlrpServer.HasImpinjCustom(adds[0]));
        Assert.False(FakeLlrpServer.HasImpinjCustom(adds[1]));
        Assert.True(reader.IsConnected);
    }

    [Fact]
    public async Task Desconectar_para_apaga_a_rospec_e_fecha()
    {
        var reader = new LlrpReader(Options());
        await reader.ConnectAsync().WithTimeout();
        await reader.StartInventoryAsync().WithTimeout();
        _server.Clear();

        await reader.DisposeAsync().WithTimeout();

        Assert.Equal([LlrpMessageType.StopRoSpec, LlrpMessageType.DeleteRoSpec, LlrpMessageType.CloseConnection], _server.ReceivedTypes());
        Assert.Equal(LlrpReader.RoSpecId, BinaryPrimitives.ReadUInt32BigEndian(_server.Last(LlrpMessageType.DeleteRoSpec).Body));
        Assert.False(reader.IsConnected);
    }

    [Fact]
    public async Task Fabrica_cria_o_driver_LLRP()
    {
        await using var reader = new RfidReaderFactory(new ReaderProtocolFactory()).Create(Options());
        Assert.IsType<LlrpReader>(reader);
        Assert.Equal($"LLRP 127.0.0.1:{_server.Port}", reader.Name);
    }
}
