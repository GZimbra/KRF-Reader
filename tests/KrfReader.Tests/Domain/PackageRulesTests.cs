using KrfReader.Domain;

namespace KrfReader.Tests.Domain;

public class PackageRulesTests
{
    private static PackageInput Input(
        string? codigo = " glt-0001 ", string? peso = null, string? comprimento = null, params PackageTagInput[] tags) =>
        new(codigo, " GLT ", " glt1165 ", comprimento, null, null, peso, "  ",
            tags.Length == 0 ? [new("e280-1160-6000-0208-0000-0A01", " frente "), new("E28011606000020800000B01")] : tags);

    [Fact]
    public void Normaliza_codigo_tipo_posicao_e_epc()
    {
        var v = PackageRules.Validate(Input());

        Assert.Equal("GLT-0001", v.Codigo);
        Assert.Equal("GLT", v.Descricao);
        Assert.Equal("GLT1165", v.TipoGlt);
        Assert.Null(v.Observacao);
        Assert.Equal("E28011606000020800000A01", v.Etiquetas[0].Epc);
        Assert.Equal("FRENTE", v.Etiquetas[0].Posicao);
        Assert.Null(v.Etiquetas[1].Posicao);
    }

    [Theory]
    [InlineData("1,85", 1.85)]
    [InlineData("61.5", 61.5)]
    [InlineData("0,0004", null)]
    [InlineData("-2", null)]
    [InlineData("abc", null)]
    public void Peso_aceita_virgula_e_recusa_invalido(string text, double? expected)
    {
        var ok = PackageRules.TryValidate(Input(peso: text), out var v, out var error);

        Assert.Equal(expected is not null, ok);
        if (expected is { } kg)
            Assert.Equal((decimal)kg, v!.PesoKg);
        else
            Assert.StartsWith("Peso inválido", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("12.5")]
    [InlineData("100001")]
    public void Dimensao_invalida(string mm)
    {
        Assert.False(PackageRules.TryValidate(Input(comprimento: mm), out _, out var error));
        Assert.StartsWith("Comprimento inválido", error);
    }

    [Fact]
    public void Exige_codigo_e_ao_menos_uma_etiqueta()
    {
        Assert.False(PackageRules.TryValidate(Input(codigo: " "), out _, out var e1));
        Assert.StartsWith("Informe o código", e1);

        Assert.False(PackageRules.TryValidate(Input() with { Etiquetas = [] }, out _, out var e2));
        Assert.StartsWith("Vincule ao menos uma etiqueta", e2);
    }

    [Fact]
    public void Recusa_epc_repetido_invalido_e_excesso_de_etiquetas()
    {
        Assert.False(PackageRules.TryValidate(Input(tags: [new("AAAA"), new("aa-aa")]), out _, out var dup));
        Assert.Contains("repetido", dup);

        Assert.False(PackageRules.TryValidate(Input(tags: [new("AAAA"), new("XYZ")]), out _, out var bad));
        Assert.StartsWith("Etiqueta 2:", bad);

        var many = Enumerable.Range(1, PackageRules.MaxEtiquetas + 1).Select(i => new PackageTagInput($"{i:X4}")).ToArray();
        Assert.False(PackageRules.TryValidate(Input(tags: many), out _, out var tooMany));
        Assert.Contains("máximo", tooMany);
    }
}
