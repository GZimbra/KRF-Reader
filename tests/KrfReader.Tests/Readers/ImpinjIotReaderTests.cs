using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using KrfReader.Application.Configuration;
using KrfReader.Application.Readers;
using KrfReader.Domain;
using KrfReader.Infrastructure.Readers.ImpinjIot;
using KrfReader.Tests.Fakes;

namespace KrfReader.Tests.Readers;

/// <summary>Driver REST do R700 contra uma API falsa (formato conforme a documentação da IoT Device Interface; sem hardware real).</summary>
public class ImpinjIotReaderTests
{
    private const string Epc = "E2801160600002081234ABCD";
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static ReaderOptions Options(bool anatel = true) => new()
    {
        Type = ReaderType.ImpinjIot,
        Tcp = new TcpReaderOptions { Host = "r700.local", Port = 443, ConnectTimeoutMs = 2000 },
        Antennas = [1, 2],
        PowerDbm = 30,
        AntennaPowerDbm = new() { [2] = 27.1 },
        EnforceAnatelBand = anatel,
        ReconnectDelayMs = 20,
        ImpinjIot = new ImpinjIotReaderOptions { Username = "root", Password = "impinj" },
        Inventory = new InventoryOptions { Session = GenSession.S2, InitialQ = 5, Target = TargetStrategy.DualAB },
    };

    private static ImpinjIotReader Reader(FakeImpinjIotHandler handler, ReaderOptions? options = null) =>
        new(options ?? Options(), null, () => handler, null);

    [Fact]
    public void Preset_tem_portas_potencia_session_e_canais_Anatel()
    {
        using var doc = JsonDocument.Parse(ImpinjIotReader.BuildPreset(Options()));
        var configs = doc.RootElement.GetProperty("antennaConfigs").EnumerateArray().ToArray();

        Assert.Equal(2, configs.Length);
        Assert.Equal(1, configs[0].GetProperty("antennaPort").GetInt32());
        Assert.Equal(3000, configs[0].GetProperty("transmitPowerCdbm").GetInt32());
        Assert.Equal(2700, configs[1].GetProperty("transmitPowerCdbm").GetInt32()); // 27,1 → passo de 0,25 dB
        Assert.Equal(2, configs[0].GetProperty("inventorySession").GetInt32());
        Assert.Equal("dual-target", configs[0].GetProperty("inventorySearchMode").GetString());
        Assert.Equal(32, configs[0].GetProperty("estimatedTagPopulation").GetInt32());
        var channels = configs[0].GetProperty("channelFrequenciesKhz").EnumerateArray().Select(c => c.GetInt32()).ToArray();
        Assert.Equal(915_250, channels[0]);
        Assert.Equal(927_250, channels[^1]);
        Assert.All(channels, c => Assert.True(AnatelBand.Contains(c)));
    }

    [Fact]
    public void Sem_Anatel_nao_restringe_canais()
    {
        using var doc = JsonDocument.Parse(ImpinjIotReader.BuildPreset(Options(anatel: false)));
        Assert.False(doc.RootElement.GetProperty("antennaConfigs")[0].TryGetProperty("channelFrequenciesKhz", out _));
    }

    [Theory]
    [InlineData(5, 1000)]
    [InlineData(27.1, 2700)]
    [InlineData(27.13, 2725)]
    [InlineData(40, 3300)]
    public void Potencia_em_cdbm(double dbm, int cdbm) => Assert.Equal(cdbm, ImpinjIotReader.ToCdbm(dbm));

    [Fact]
    public void Evento_de_tag_com_epcHex_ou_base64()
    {
        var hex = ImpinjIotReader.ParseEvent(
            $$$"""{"timestamp":"2026-10-28T12:00:00Z","eventType":"tagInventory","tagInventoryEvent":{"epcHex":"{{{Epc}}}","antennaPort":3,"peakRssiCdbm":-5650}}""", Now);
        var b64 = ImpinjIotReader.ParseEvent(
            $$$"""{"tagInventoryEvent":{"epc":"{{{Convert.ToBase64String(Convert.FromHexString(Epc))}}}","antennaPort":2}}""", Now);

        Assert.Equal(new TagRead(Epc, -56.5, 3, Now), hex);
        Assert.Equal(Epc, b64?.Epc);
        Assert.Equal(2, b64?.AntennaId);
        Assert.Null(ImpinjIotReader.ParseEvent("""{"inventoryStatusEvent":{"inventoryStatus":"running"}}""", Now));
    }

    [Fact]
    public async Task Conectar_grava_preset_e_inventario_le_o_fluxo()
    {
        var api = new FakeImpinjIotHandler { Status = "running" };
        await using var reader = Reader(api);
        var reads = new ConcurrentQueue<TagRead>();
        reader.TagRead += (_, e) => reads.Enqueue(e.Read);

        await reader.ConnectAsync().WithTimeout();
        Assert.Equal(
            ["GET /api/v1/status", "POST /api/v1/profiles/stop", "PUT /api/v1/profiles/inventory/presets/krf-reader", "GET /api/v1/system"],
            api.Calls());
        Assert.Equal($"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("root:impinj"))}", api.Requests.First().Auth);
        Assert.Contains("R700", reader.Info?.Description);

        await reader.StartInventoryAsync().WithTimeout();
        await TestUtil.Eventually(() => api.StreamsOpened == 1, "fluxo aberto");
        api.PushEvent($$$"""{"tagInventoryEvent":{"epcHex":"{{{Epc}}}","antennaPort":1,"peakRssiCdbm":-6000}}""");
        await TestUtil.Eventually(() => reads.Count == 1, "leitura do fluxo");

        await reader.StopInventoryAsync().WithTimeout();
        Assert.Equal("POST /api/v1/profiles/stop", api.Calls()[^1]);
        api.PushEvent($$$"""{"tagInventoryEvent":{"epcHex":"{{{Epc}}}","antennaPort":1,"peakRssiCdbm":-6000}}""");
        await Task.Delay(50);
        Assert.Single(reads);
    }

    [Fact]
    public async Task Fluxo_caido_reabre_e_reinicia_se_o_leitor_parou()
    {
        var api = new FakeImpinjIotHandler();
        await using var reader = Reader(api);
        var reads = new ConcurrentQueue<TagRead>();
        reader.TagRead += (_, e) => reads.Enqueue(e.Read);
        await reader.ConnectAsync().WithTimeout();
        await reader.StartInventoryAsync().WithTimeout();
        await TestUtil.Eventually(() => api.StreamsOpened == 1, "fluxo aberto");

        api.Status = "idle"; // leitor reiniciou
        api.EndStream();

        await TestUtil.Eventually(() => api.StreamsOpened == 2, "fluxo reaberto");
        Assert.Equal(2, api.Calls().Count(c => c.EndsWith("/krf-reader/start")));
        Assert.Equal(1, reader.GetDiagnostics().LinkDrops);
        api.PushEvent($$$"""{"tagInventoryEvent":{"epcHex":"{{{Epc}}}","antennaPort":1}}""");
        await TestUtil.Eventually(() => reads.Count == 1, "leitura após reabrir");
    }

    [Fact]
    public async Task Preset_recusado_falha_a_conexao_com_o_motivo()
    {
        var api = new FakeImpinjIotHandler { PresetStatus = HttpStatusCode.BadRequest };
        await using var reader = Reader(api);

        var ex = await Assert.ThrowsAsync<ImpinjIotException>(() => reader.ConnectAsync().WithTimeout());

        Assert.Contains("HTTP 400", ex.Message);
        Assert.Contains("invalid preset", ex.Message);
        Assert.False(reader.IsConnected);
    }
}
