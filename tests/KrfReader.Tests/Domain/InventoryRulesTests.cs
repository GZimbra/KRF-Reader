using KrfReader.Domain;

namespace KrfReader.Tests.Domain;

public class InventoryRulesTests
{
    public static TheoryData<TagStatus?, OperationMode, TagStatus, EventType?> Combinations => new()
    {
        { null, OperationMode.Entrada, TagStatus.Dentro, EventType.Criacao },
        { null, OperationMode.Saida, TagStatus.Fora, EventType.SaidaSemEntrada },
        { TagStatus.Dentro, OperationMode.Entrada, TagStatus.Dentro, null },
        { TagStatus.Fora, OperationMode.Saida, TagStatus.Fora, null },
        { TagStatus.Fora, OperationMode.Entrada, TagStatus.Dentro, EventType.MudancaStatus },
        { TagStatus.Dentro, OperationMode.Saida, TagStatus.Fora, EventType.MudancaStatus },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Decide_cobre_as_seis_combinacoes(TagStatus? current, OperationMode mode, TagStatus expectedStatus, EventType? expectedEvent)
    {
        var d = InventoryRules.Decide(current, mode);

        Assert.Equal(expectedStatus, d.NewStatus);
        Assert.Equal(expectedEvent, d.Event);
        Assert.Equal(expectedEvent is not null, d.ChangesState);
        Assert.Equal(InventoryRules.TargetStatus(mode), d.NewStatus);
    }

    [Fact]
    public void Desconhecida_em_saida_vira_fora_com_anomalia()
    {
        var d = InventoryRules.Decide(null, OperationMode.Saida);

        Assert.Equal(TagStatus.Fora, d.NewStatus);
        Assert.Equal(EventType.SaidaSemEntrada, d.Event);
        Assert.True(d.IsAnomaly);
    }

    [Theory]
    [InlineData(null, OperationMode.Entrada)]
    [InlineData(TagStatus.Fora, OperationMode.Entrada)]
    [InlineData(TagStatus.Dentro, OperationMode.Saida)]
    [InlineData(TagStatus.Dentro, OperationMode.Entrada)]
    [InlineData(TagStatus.Fora, OperationMode.Saida)]
    public void Somente_desconhecida_em_saida_e_anomalia(TagStatus? current, OperationMode mode)
    {
        Assert.False(InventoryRules.Decide(current, mode).IsAnomaly);
    }

    [Theory]
    [InlineData(TagStatus.Dentro, OperationMode.Entrada)]
    [InlineData(TagStatus.Fora, OperationMode.Saida)]
    public void Releitura_no_mesmo_estado_e_idempotente(TagStatus current, OperationMode mode)
    {
        var d = InventoryRules.Decide(current, mode);

        Assert.Null(d.Event);
        Assert.False(d.ChangesState);
        Assert.False(d.IsAnomaly);
        Assert.Equal(current, d.NewStatus);
    }

    [Fact]
    public void Modo_invalido_lanca()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InventoryRules.Decide(null, (OperationMode)99));
    }

    [Fact]
    public void InventoryEvent_IsAnomaly_somente_para_SaidaSemEntrada()
    {
        var now = DateTime.UtcNow;
        Assert.True(new InventoryEvent(1, "AA", OperationMode.Saida, EventType.SaidaSemEntrada, now, 1, -40).IsAnomaly);
        Assert.False(new InventoryEvent(2, "AA", OperationMode.Entrada, EventType.Criacao, now, 1, -40).IsAnomaly);
        Assert.False(new InventoryEvent(3, "AA", OperationMode.Saida, EventType.MudancaStatus, now, 1, -40).IsAnomaly);
    }
}
