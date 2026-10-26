using KrfReader.Domain;

namespace KrfReader.Tests.Domain;

public class DbLiteralsTests
{
    [Theory]
    [InlineData(TagStatus.Dentro, "DENTRO")]
    [InlineData(TagStatus.Fora, "FORA")]
    public void Status_roundtrip(TagStatus value, string literal)
    {
        Assert.Equal(literal, value.ToDb());
        Assert.Equal(value, DbLiterals.ParseStatus(literal));
    }

    [Theory]
    [InlineData(OperationMode.Entrada, "ENTRADA")]
    [InlineData(OperationMode.Saida, "SAIDA")]
    public void Modo_roundtrip(OperationMode value, string literal)
    {
        Assert.Equal(literal, value.ToDb());
        Assert.Equal(value, DbLiterals.ParseMode(literal));
    }

    [Theory]
    [InlineData(EventType.Criacao, "CRIACAO")]
    [InlineData(EventType.MudancaStatus, "MUDANCA_STATUS")]
    [InlineData(EventType.SaidaSemEntrada, "SAIDA_SEM_ENTRADA")]
    public void Evento_roundtrip(EventType value, string literal)
    {
        Assert.Equal(literal, value.ToDb());
        Assert.Equal(value, DbLiterals.ParseEventType(literal));
    }

    [Fact]
    public void Todos_os_valores_dos_enums_tem_literal()
    {
        foreach (var s in Enum.GetValues<TagStatus>())
            Assert.Equal(s, DbLiterals.ParseStatus(s.ToDb()));
        foreach (var m in Enum.GetValues<OperationMode>())
            Assert.Equal(m, DbLiterals.ParseMode(m.ToDb()));
        foreach (var e in Enum.GetValues<EventType>())
            Assert.Equal(e, DbLiterals.ParseEventType(e.ToDb()));
    }

    [Theory]
    [InlineData("dentro")]
    [InlineData("")]
    [InlineData("SAÍDA")]
    public void Parse_invalido_lanca_FormatException(string literal)
    {
        Assert.Throws<FormatException>(() => DbLiterals.ParseStatus(literal));
        Assert.Throws<FormatException>(() => DbLiterals.ParseMode(literal));
        Assert.Throws<FormatException>(() => DbLiterals.ParseEventType(literal));
    }

    [Fact]
    public void ToDb_valor_fora_do_enum_lanca()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((TagStatus)0).ToDb());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((OperationMode)0).ToDb());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((EventType)0).ToDb());
    }
}
