using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KrfReader.Application.Configuration;
using KrfReader.Application.Pipeline;
using KrfReader.Application.Readers;

namespace KrfReader.UI.ViewModels;

/// <summary>
/// Painel de passagem da aba Operação (carga atravessando o campo) e campos de anticolisão da aba Configurações.
/// Eventos do <see cref="PassageMonitor"/> chegam na thread do timer e fazem Post para a UI.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public string[] SessionOptions { get; } = ["S0", "S1", "S2", "S3"];

    public string[] TargetOptions { get; } = ["A fixo", "A ↔ B por rodada"];

    [ObservableProperty] private int _inventorySessionIndex;
    [ObservableProperty] private int _inventoryTargetIndex;
    [ObservableProperty] private decimal? _inventoryInitialQ;
    [ObservableProperty] private bool _inventoryAdaptiveQ;
    [ObservableProperty] private decimal? _inventoryDwellMs;
    [ObservableProperty] private string? _selectEpcPrefix;

    [ObservableProperty] private bool _hasPassage;
    [ObservableProperty] private string _passageTitle = "PASSAGEM";
    [ObservableProperty] private string _passageStateText = "AGUARDANDO CARGA";
    [ObservableProperty] private string _verdictText = "—";
    [ObservableProperty] private bool _isVerdictOk;
    [ObservableProperty] private bool _isVerdictWarn;
    [ObservableProperty] private bool _isVerdictBad;
    [ObservableProperty] private string _verdictHint = "Selecione ENTRADA ou SAÍDA e passe a carga pela antena.";
    [ObservableProperty] private int _passageTagCount;
    [ObservableProperty] private string _passageStableText = "0/0";
    [ObservableProperty] private string _passageMissedText = "—";
    [ObservableProperty] private long _passageReads;
    [ObservableProperty] private string _passageDetailText = "";
    [ObservableProperty] private string _passageIssuesText = "";
    [ObservableProperty] private IReadOnlyList<PassageTagRow> _passageTags = [];
    [ObservableProperty] private string _inventoryControlText = "ANTICOLISÃO —";

    private int _requiredStableRounds = 3;

    private void InitializeInventoryFields(ReaderOptions options, PassageOptions passage)
    {
        var inventory = options.Inventory ?? new InventoryOptions();
        InventorySessionIndex = Math.Clamp((int)inventory.Session, 0, 3);
        InventoryTargetIndex = inventory.Target == TargetStrategy.DualAB ? 1 : 0;
        InventoryInitialQ = Math.Clamp(inventory.InitialQ, InventoryParameters.MinQ, InventoryParameters.MaxQ);
        InventoryAdaptiveQ = inventory.AdaptiveQ;
        InventoryDwellMs = Math.Clamp(inventory.TargetDwellMs, 100, 5000);
        SelectEpcPrefix = inventory.SelectEpcPrefix;
        _requiredStableRounds = passage.StableRounds;
        PassageStableText = $"0/{passage.StableRounds}";
    }

    private bool TryBuildInventoryOptions(out InventoryOptions inventory, out string error)
    {
        var current = _baseReaderOptions.Inventory ?? new InventoryOptions();
        inventory = null!;
        if (InventoryInitialQ is not { } q || q < InventoryParameters.MinQ || q > InventoryParameters.MaxQ)
        {
            error = "Q inicial deve estar entre 0 e 15.";
            return false;
        }

        if (InventoryDwellMs is not { } dwell || dwell < 100 || dwell > 5000)
        {
            error = "Duração da rodada deve estar entre 100 e 5000 ms.";
            return false;
        }

        var prefix = (SelectEpcPrefix ?? "").Trim();
        var hex = KrfReader.Domain.Epc.Normalize(prefix);
        if (prefix.Length > 0 && (hex is null || hex.Length % 2 != 0 || hex.Length > 62))
        {
            error = "Prefixo de Select inválido: use hexadecimal com número par de dígitos (ex.: E280).";
            return false;
        }

        inventory = new InventoryOptions
        {
            Session = (GenSession)Math.Clamp(InventorySessionIndex, 0, 3),
            Target = InventoryTargetIndex == 1 ? TargetStrategy.DualAB : TargetStrategy.SingleA,
            InitialQ = (int)q,
            AdaptiveQ = InventoryAdaptiveQ,
            MinQ = current.MinQ,
            MaxQ = current.MaxQ,
            TargetDwellMs = (int)dwell,
            SelectEpcPrefix = hex ?? "",
            CommandGapMs = current.CommandGapMs,
        };
        error = "";
        return true;
    }

    private void AttachPassage(PassageMonitor monitor)
    {
        monitor.PassageUpdated += OnPassageUpdated;
        monitor.PassageClosed += OnPassageClosed;
    }

    private void DetachPassage(PassageMonitor monitor)
    {
        monitor.PassageUpdated -= OnPassageUpdated;
        monitor.PassageClosed -= OnPassageClosed;
    }

    private void OnPassageUpdated(object? sender, PassageSnapshot snapshot) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, _current?.Monitor))
                ShowPassage(snapshot);
        });

    private void OnPassageClosed(object? sender, PassageSnapshot snapshot)
    {
        _ = SavePassageAsync(snapshot);
        Dispatcher.UIThread.Post(() =>
        {
            ShowPassage(snapshot);
            if (snapshot.Verdict != PassageVerdict.Confiavel)
                ReportError($"Passagem #{snapshot.Id}: {VerdictLabel(snapshot.Verdict)}. {string.Join(" ", snapshot.Issues)}");
        });
    }

    /// <summary>Grava a passagem fechada (vínculo das movimentações). Falha não interrompe a operação.</summary>
    private async Task SavePassageAsync(PassageSnapshot snapshot)
    {
        if (snapshot.Mode is null || snapshot.DistinctTags == 0)
            return;
        try
        {
            await Task.Run(() => _repository.SavePassageAsync(snapshot));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => ReportError($"Falha ao gravar passagem #{snapshot.Id}: {ex.Message}"));
        }
    }

    private void ShowPassage(PassageSnapshot s)
    {
        HasPassage = true;
        var live = s.State == PassageState.Active;
        PassageTitle = $"PASSAGEM #{s.Id} · {Display.Mode(s.Mode)}";
        PassageStateText = live
            ? "AO VIVO"
            : $"FECHADA {Display.LocalTime(s.EndedUtc ?? s.StartedUtc)} · {s.CloseReason?.ToUpperInvariant()}";

        VerdictText = live && s.Verdict is PassageVerdict.NaoConvergiu ? "LENDO..." : VerdictLabel(s.Verdict);
        IsVerdictOk = s.Verdict == PassageVerdict.Confiavel;
        IsVerdictWarn = s.Verdict == PassageVerdict.Atencao || (live && s.Verdict == PassageVerdict.NaoConvergiu);
        IsVerdictBad = s.Verdict == PassageVerdict.DadosIncompletos || (!live && s.Verdict == PassageVerdict.NaoConvergiu);
        VerdictHint = (live, s.Verdict) switch
        {
            (true, PassageVerdict.NaoConvergiu) => "Ainda aparecem itens novos. Mantenha a carga no campo.",
            (true, PassageVerdict.Confiavel) => "Descoberta convergiu. A carga pode seguir.",
            (_, PassageVerdict.Confiavel) => "Todas as tags que responderam foram lidas e a descoberta convergiu.",
            (_, PassageVerdict.Atencao) => "Convergiu, mas há tag com leitura marginal. Conferir ou repassar.",
            (_, PassageVerdict.NaoConvergiu) => "A carga saiu enquanto apareciam itens novos. Repassar a carga.",
            _ => "Leituras perdidas fora do RF. Repassar a carga.",
        };

        PassageTagCount = s.DistinctItems;
        PassageStableText = $"{s.StableRounds}/{_requiredStableRounds}";
        PassageMissedText = s.EstimatedMissed.ToString("0.0", CultureInfo.CurrentCulture);
        PassageReads = s.TotalReads;
        PassageDetailText =
            $"etiquetas {s.DistinctTags} · embalagens {s.Packages}" +
            (s.PartialPackages > 0 ? $" ({s.PartialPackages} com etiqueta não lida)" : "") + " · " +
            $"rodadas {s.Rounds} · novos na última {s.NewInLastRound} · responderam {s.RespondingLastRound} · " +
            $"marginais {s.MarginalTags} · {s.Duration.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s";
        PassageIssuesText = string.Join("  ", s.Issues);
        PassageTags = s.Tags.Select(t => new PassageTagRow(t)).ToArray();
    }

    private void ResetPassage()
    {
        HasPassage = false;
        PassageTitle = "PASSAGEM";
        PassageStateText = "AGUARDANDO CARGA";
        VerdictText = "—";
        IsVerdictOk = IsVerdictWarn = IsVerdictBad = false;
        VerdictHint = "Selecione ENTRADA ou SAÍDA e passe a carga pela antena.";
        PassageTagCount = 0;
        PassageStableText = $"0/{_requiredStableRounds}";
        PassageMissedText = "—";
        PassageReads = 0;
        PassageDetailText = "";
        PassageIssuesText = "";
        PassageTags = [];
        InventoryControlText = "ANTICOLISÃO —";
    }

    /// <summary>UI thread, a cada tick do timer.</summary>
    private void UpdateInventoryControl(PassageMonitor monitor)
    {
        var c = monitor.ControlState;
        InventoryControlText = c.TuningSupported
            ? $"Q {c.Parameters.Q} ({1 << c.Parameters.Q} slots) · {c.Parameters.Session} · TARGET {c.Parameters.Target}" +
              (c.Parameters.UseSelect ? " · SELECT" : "") + $" · rodadas vazias {c.EmptyRounds:N0} · quadros inválidos {c.InvalidFrames:N0}"
            : "ANTICOLISÃO FIXA NO LEITOR (protocolo sem ajuste de Q/target)";
    }

    private static string VerdictLabel(PassageVerdict verdict) => verdict switch
    {
        PassageVerdict.Confiavel => "CONFIÁVEL",
        PassageVerdict.Atencao => "ATENÇÃO",
        PassageVerdict.NaoConvergiu => "NÃO CONVERGIU",
        _ => "DADOS INCOMPLETOS",
    };
}

/// <summary>Linha da tabela de tags da passagem.</summary>
public sealed class PassageTagRow(PassageTag tag)
{
    public string Epc { get; } = tag.PackageCodigo is { } codigo ? $"{codigo} · {tag.Epc}" : tag.Epc;
    public int Reads { get; } = tag.Reads;
    public int Rounds { get; } = tag.Rounds;
    public string MaxRssi { get; } = Display.Rssi(tag.MaxRssi);
    public double MaxRssiValue { get; } = tag.MaxRssi;
    public bool IsMarginal { get; } = tag.IsMarginal;
    public string Flag { get; } = tag.IsMarginal ? "⚠ MARGINAL" : "OK";
}
