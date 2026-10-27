using KrfReader.Domain;

namespace KrfReader.Tests.Domain;

public class TagRegistrationRulesTests
{
    private static TagRegistrationInput Input(string? epc = "E28011606000020812345678", string? descricao = "Palete A",
        string? sku = null, string? observacao = null) => new(epc, descricao, sku, observacao);

    [Fact]
    public void Normaliza_EPC_e_apara_textos()
    {
        var v = TagRegistrationRules.Validate(Input(" e280-1160 6000:0208 1234 5678 ", "  Palete A  ", "  SKU-1 ", "   "));

        Assert.Equal("E28011606000020812345678", v.Epc);
        Assert.Equal("Palete A", v.Descricao);
        Assert.Equal("SKU-1", v.Sku);
        Assert.Null(v.Observacao);
    }

    [Theory]
    [InlineData("E280")]                       // 16 bits
    [InlineData("E28011606000020812345678")]   // EPC-96
    [InlineData("E2801160600002081234567890ABCDEF")] // 128 bits
    public void Aceita_EPC_em_palavras_de_16_bits(string epc)
    {
        Assert.True(TagRegistrationRules.TryValidate(Input(epc), out var v, out var error), error);
        Assert.Equal(epc, v!.Epc);
    }

    [Fact]
    public void Aceita_EPC_de_496_bits_e_rejeita_maior()
    {
        Assert.True(TagRegistrationRules.TryValidate(Input(new string('A', 124)), out _, out _));
        Assert.False(TagRegistrationRules.TryValidate(Input(new string('A', 128)), out _, out _));
    }

    [Theory]
    [InlineData(null, "Informe o EPC")]
    [InlineData("   ", "Informe o EPC")]
    [InlineData("E28G", "hexadecimais")]
    [InlineData("E2801", "múltiplo de 4")]
    [InlineData("E28", "múltiplo de 4")]
    [InlineData("E28011606000020812345", "múltiplo de 4")]
    public void Rejeita_EPC_invalido(string? epc, string expectedError)
    {
        Assert.False(TagRegistrationRules.TryValidate(Input(epc), out var v, out var error));
        Assert.Null(v);
        Assert.Contains(expectedError, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void Descricao_e_obrigatoria(string? descricao)
    {
        Assert.False(TagRegistrationRules.TryValidate(Input(descricao: descricao), out _, out var error));
        Assert.Contains("descrição", error);
    }

    [Fact]
    public void Rejeita_campos_acima_do_limite()
    {
        Assert.False(TagRegistrationRules.TryValidate(Input(descricao: new string('x', 201)), out _, out _));
        Assert.False(TagRegistrationRules.TryValidate(Input(sku: new string('x', 65)), out _, out _));
        Assert.False(TagRegistrationRules.TryValidate(Input(observacao: new string('x', 501)), out _, out _));

        Assert.True(TagRegistrationRules.TryValidate(
            Input(descricao: new string('x', 200), sku: new string('x', 64), observacao: new string('x', 500)), out _, out _));
    }

    [Fact]
    public void Validate_lanca_ArgumentException_com_a_mensagem()
    {
        var ex = Assert.Throws<ArgumentException>(() => TagRegistrationRules.Validate(Input(epc: "XYZ")));
        Assert.Contains("hexadecimais", ex.Message);
    }
}
