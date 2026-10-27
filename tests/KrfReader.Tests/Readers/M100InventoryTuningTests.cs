using KrfReader.Application.Readers;
using KrfReader.Infrastructure.Readers.Protocols;

namespace KrfReader.Tests.Readers;

/// <summary>Comandos de anticolisão do M100 (Set Query 0E, Set Select 0C, Select Mode 12) e contadores.</summary>
public class M100InventoryTuningTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SetQuery_reproduz_exemplo_do_manual()
    {
        // Manual M100: BB 00 0E 00 02 10 20 40 7E = DR 8, M 1, TRext 1, Sel 00, S0, target A, Q 4.
        var frame = M100Protocol.CreateR200().BuildSetQuery(new InventoryParameters(GenSession.S0, InventoryTarget.A, 4, false));

        Assert.Equal("AA000E00021020" + "40" + "DD", Convert.ToHexString(frame));
    }

    [Theory]
    [InlineData(GenSession.S2, InventoryTarget.A, 6, false, 0x1230)] // 0001 00 10 0 0110 000
    [InlineData(GenSession.S2, InventoryTarget.B, 6, false, 0x12B0)] // target B = bit 7
    [InlineData(GenSession.S1, InventoryTarget.A, 15, true, 0x1D78)] // Sel 11, S1, Q 15
    [InlineData(GenSession.S3, InventoryTarget.B, 0, false, 0x1380)]
    public void SetQuery_codifica_session_target_q_e_sel(GenSession session, InventoryTarget target, int q, bool select, int word)
    {
        var frame = M100Protocol.CreateR200().BuildSetQuery(new InventoryParameters(session, target, q, select));

        Assert.Equal(0x0E, frame[2]);
        Assert.Equal(word, (frame[5] << 8) | frame[6]);
    }

    [Fact]
    public void SetQuery_limita_q_a_15()
    {
        var frame = M100Protocol.CreateR200().BuildSetQuery(new InventoryParameters(GenSession.S0, InventoryTarget.A, 99, false));

        Assert.Equal(15, (((frame[5] << 8) | frame[6]) >> 3) & 0xF);
    }

    [Fact]
    public void Perfil_sem_prefixo_desliga_select()
    {
        var p = M100Protocol.CreateR200();
        var commands = p.BuildProfileCommands(new InventoryProfile(new InventoryParameters(GenSession.S2, InventoryTarget.A, 4, false), ""));

        Assert.Equal(2, commands.Count);
        Assert.Equal("AA0012000101" + "14" + "DD", Convert.ToHexString(commands[0]));
        Assert.Equal(0x0E, commands[1][2]);
        Assert.Equal(0, (commands[1][5] >> 2) & 0b11); // Sel = todas
    }

    [Fact]
    public void Perfil_com_prefixo_envia_select_no_banco_epc_e_query_com_sel_sl()
    {
        var p = M100Protocol.CreateR200();
        var commands = p.BuildProfileCommands(new InventoryProfile(new InventoryParameters(GenSession.S2, InventoryTarget.A, 4, false), "E2 80 11"));

        Assert.Equal(3, commands.Count);
        var select = commands[0];
        Assert.Equal(0x0C, select[2]);
        // SelParam 81 (SL, ação 000, banco EPC) | ponteiro 0x20 | 24 bits | sem truncar | máscara E28011.
        Assert.Equal("81000000201800E28011", Convert.ToHexString(select.AsSpan(5, select.Length - 7)));
        Assert.Equal("AA0012000100" + "13" + "DD", Convert.ToHexString(commands[1]));
        Assert.Equal(0b11, (commands[2][5] >> 2) & 0b11); // Sel = SL
    }

    [Theory]
    [InlineData("E28")]
    [InlineData("XYZ1")]
    public void Prefixo_invalido_e_rejeitado(string prefix)
    {
        Assert.Throws<ArgumentException>(() => M100Protocol.ParseSelectMask(prefix));
    }

    [Fact]
    public void Troca_de_parametros_com_inventario_ativo_para_configura_e_reinicia()
    {
        var p = M100Protocol.CreateR200();
        var commands = p.BuildApplyCommands(new InventoryParameters(GenSession.S2, InventoryTarget.B, 6, false), inventoryRunning: true);

        Assert.Equal([0x28, 0x0E, 0x27], commands.Select(c => c[2]).ToArray());
        Assert.Single(p.BuildApplyCommands(new InventoryParameters(GenSession.S2, InventoryTarget.B, 6, false), inventoryRunning: false));
    }

    [Fact]
    public void Erro_0x15_conta_rodada_vazia()
    {
        var p = M100Protocol.CreateR200();

        p.Feed(Convert.FromHexString("AA01FF00011516DD" + "AA01FF00011516DD"), Now);

        Assert.Equal(2, p.EmptyRounds);
        Assert.Equal(0, p.InvalidFrames);
    }
}
