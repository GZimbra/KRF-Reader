using System.Buffers.Binary;
using KrfReader.Application.Readers;
using KrfReader.Infrastructure.Readers.Llrp;
using KrfReader.Tests.Fakes;

namespace KrfReader.Tests.Readers;

/// <summary>Codificação LLRP 1.0.1 conforme a especificação EPCglobal (sem hardware real).</summary>
public class LlrpProtocolTests
{
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static readonly List<LlrpPowerLevel> Power =
        Enumerable.Range(1, 93).Select(i => new LlrpPowerLevel((ushort)i, 10 + 0.25 * (i - 1))).ToList();

    private static uint[] Range(int fromKhz, int toKhz) =>
        Enumerable.Range(0, (toKhz - fromKhz) / 500 + 1).Select(i => (uint)(fromKhz + 500 * i)).ToArray();

    [Fact]
    public void Cabecalho_tem_versao_1_tipo_comprimento_e_id()
    {
        var frame = LlrpProtocol.RoSpecCommand(LlrpMessageType.StartRoSpec, 0x01020304, 77);

        Assert.Equal(14, frame.Length);
        Assert.Equal((1 << 10) | 22, BinaryPrimitives.ReadUInt16BigEndian(frame));
        Assert.Equal(14u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(2)));
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(6)));
        Assert.Equal(77u, BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(10)));
    }

    [Fact]
    public void Keepalive_spec_periodico_e_reset()
    {
        var frame = LlrpProtocol.SetReaderConfig(1, resetToFactoryDefault: true, keepaliveMs: 5000);
        var body = frame.AsSpan(LlrpWriter.HeaderLength);

        Assert.Equal(0x80, body[0]);
        var reader = new LlrpParamReader(body[1..]);
        Assert.True(reader.TryRead(out var keepalive));
        Assert.Equal(LlrpParamType.KeepaliveSpec, keepalive.Type);
        Assert.Equal(1, keepalive.Value[0]);
        Assert.Equal(5000u, BinaryPrimitives.ReadUInt32BigEndian(keepalive.Value[1..]));
    }

    [Fact]
    public void Capacidades_sao_interpretadas()
    {
        var body = FakeLlrpServer.CapabilitiesBody(LlrpProtocol.ImpinjPen, 4, Power, [new LlrpHopTable(1, Range(915_250, 927_250))]);

        var caps = LlrpProtocol.ParseCapabilities(body);

        Assert.Equal(4, caps.MaxAntennas);
        Assert.Equal(LlrpProtocol.ImpinjPen, caps.ManufacturerPen);
        Assert.Equal(2001u, caps.ModelName);
        Assert.Equal("9.9.9", caps.Firmware);
        Assert.Equal(76, caps.CountryCode);
        Assert.Equal(93, caps.PowerLevels.Count);
        Assert.Equal(33, caps.PowerLevels[^1].Dbm);
        Assert.True(caps.Hopping);
        Assert.Equal(25, Assert.Single(caps.HopTables).FrequenciesKhz.Count);
    }

    [Fact]
    public void Tabela_FCC_inteira_e_recusada_com_Anatel_exigida()
    {
        var caps = LlrpProtocol.ParseCapabilities(
            FakeLlrpServer.CapabilitiesBody(LlrpProtocol.ImpinjPen, 4, Power, [new LlrpHopTable(1, Range(902_750, 927_250))]));

        var ex = Assert.Throws<LlrpException>(() => LlrpProtocol.SelectRfPlan(caps, enforceAnatel: true));
        Assert.Contains("902.75", ex.Message.Replace(',', '.'));
        Assert.Contains("Impinj IoT", ex.Message);

        Assert.Equal(1, LlrpProtocol.SelectRfPlan(caps, enforceAnatel: false).HopTableId);
    }

    [Fact]
    public void Escolhe_a_tabela_de_salto_dentro_da_faixa()
    {
        var caps = LlrpProtocol.ParseCapabilities(FakeLlrpServer.CapabilitiesBody(LlrpProtocol.ZebraPen, 4, Power,
        [
            new LlrpHopTable(1, Range(902_750, 927_250)),
            new LlrpHopTable(2, Range(915_750, 927_250)),
        ]));

        var plan = LlrpProtocol.SelectRfPlan(caps, enforceAnatel: true);

        Assert.Equal(2, plan.HopTableId);
        Assert.Equal(1, plan.ChannelIndex);
    }

    [Fact]
    public void Canal_fixo_escolhe_o_primeiro_dentro_da_faixa()
    {
        var caps = LlrpProtocol.ParseCapabilities(
            FakeLlrpServer.CapabilitiesBody(LlrpProtocol.ZebraPen, 4, Power, [], fixedFrequencies: [865_700u, 916_000u, 920_000u]));

        var plan = LlrpProtocol.SelectRfPlan(caps, enforceAnatel: true);

        Assert.False(caps.Hopping);
        Assert.Equal(2, plan.ChannelIndex);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(27.6, 27.5)]  // nunca passa do pedido
    [InlineData(40, 33)]
    [InlineData(5, 10)]       // abaixo da tabela: menor potência
    public void Potencia_escolhe_o_maior_indice_sem_exceder(double requested, double expected)
    {
        Assert.Equal(expected, LlrpProtocol.SelectPower(Power, requested).Dbm);
    }

    [Fact]
    public void Relatorio_com_EPCData_e_EPC96()
    {
        var epc96 = FakeLlrpServer.Tlv(LlrpParamType.TagReportData, t =>
        {
            t.U8(0x80 | LlrpParamType.Epc96).Bytes(Convert.FromHexString("E2801160600002081234ABCD"));
            t.U8(0x80 | LlrpParamType.AntennaId).U16(3);
            t.U8(0x80 | LlrpParamType.PeakRssi).U8(unchecked((byte)-48));
            t.U8(0x80 | LlrpParamType.FirstSeenTimestampUtc).U32(0).U32(1);
        });
        var body = new[] { FakeLlrpServer.TagReport("300833B2DDD9014000000001", 2, -61), epc96 }.SelectMany(b => b).ToArray();

        var reads = LlrpProtocol.ParseReport(body, Now, out var malformed);

        Assert.Equal(0, malformed);
        Assert.Equal(2, reads.Count);
        Assert.Equal(new TagReadExpectation("300833B2DDD9014000000001", -61, 2), new TagReadExpectation(reads[0].Epc, reads[0].Rssi, reads[0].AntennaId));
        Assert.Equal(new TagReadExpectation("E2801160600002081234ABCD", -48, 3), new TagReadExpectation(reads[1].Epc, reads[1].Rssi, reads[1].AntennaId));
        Assert.All(reads, r => Assert.Equal(Now, r.TimestampUtc));
    }

    [Fact]
    public void TV_desconhecido_conta_como_relatorio_malformado_sem_derrubar()
    {
        var bad = FakeLlrpServer.Tlv(LlrpParamType.TagReportData, t =>
        {
            t.U8(0x80 | LlrpParamType.Epc96).Bytes(Convert.FromHexString("E2801160600002081234ABCD"));
            t.U8(0x80 | 99).U16(1);
        });

        var reads = LlrpProtocol.ParseReport(bad, Now, out var malformed);

        Assert.Single(reads);
        Assert.Equal(1, malformed);
    }

    [Fact]
    public void RoSpec_tem_uma_AntennaConfiguration_por_porta_com_indice_de_potencia_e_session()
    {
        var settings = new LlrpRoSpecSettings(9,
            [new LlrpAntennaSetting(1, 81, 30), new LlrpAntennaSetting(3, 69, 27)],
            new LlrpRfPlan(2, 1, ""), GenSession.S2, 32, LlrpProtocol.ImpinjDualTarget);

        var frame = LlrpProtocol.AddRoSpec(1, settings);
        var configs = Find(frame.AsSpan(LlrpWriter.HeaderLength), LlrpParamType.AntennaConfiguration);

        Assert.Equal(2, configs.Count);
        var first = configs[0];
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(first));
        var transmitter = Find(first.AsSpan(2), LlrpParamType.RfTransmitter).Single();
        Assert.Equal([0, 2, 0, 1, 0, 81], transmitter);
        var singulation = Find(first.AsSpan(2), LlrpParamType.C1G2SingulationControl).Single();
        Assert.Equal(2 << 6, singulation[0]);
        Assert.Equal(32, BinaryPrimitives.ReadUInt16BigEndian(singulation.AsSpan(1)));
        var custom = Find(first.AsSpan(2), LlrpParamType.Custom).Single();
        Assert.Equal(LlrpProtocol.ImpinjPen, BinaryPrimitives.ReadUInt32BigEndian(custom));
        Assert.Equal(LlrpProtocol.ImpinjDualTarget, BinaryPrimitives.ReadUInt16BigEndian(custom.AsSpan(8)));
        Assert.Equal(69, BinaryPrimitives.ReadUInt16BigEndian(Find(configs[1].AsSpan(2), LlrpParamType.RfTransmitter).Single().AsSpan(4)));
    }

    [Fact]
    public void Evento_de_conexao_recusada()
    {
        var w = new LlrpWriter();
        w.Tlv(LlrpParamType.ReaderEventNotificationData, d =>
        {
            d.Tlv(128, t => t.U32(0).U32(0));
            d.Tlv(LlrpParamType.ConnectionAttemptEvent, e => e.U16(2));
        });

        Assert.Equal((ushort)2, LlrpProtocol.ParseConnectionAttempt(w.ToArray()));
        Assert.Contains("outro cliente", LlrpProtocol.DescribeConnectionAttempt(2));
    }

    private readonly record struct TagReadExpectation(string Epc, double Rssi, int Antenna);

    /// <summary>Busca recursiva (TLV) de todos os parâmetros do tipo pedido; devolve os valores.</summary>
    private static List<byte[]> Find(ReadOnlySpan<byte> data, ushort type)
    {
        var found = new List<byte[]>();
        // Varredura byte a byte: TLVs aninhados começam em offsets variados dentro de campos fixos.
        for (var i = 0; i + 4 <= data.Length; i++)
        {
            var t = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(data[i..]) & 0x3FF);
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            if ((data[i] & 0xFC) == 0 && t == type && length >= 4 && i + length <= data.Length)
            {
                found.Add(data.Slice(i + 4, length - 4).ToArray());
                i += length - 1;
            }
        }

        return found;
    }
}
