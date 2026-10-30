using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;

namespace KrfReader.UI.ViewModels;

/// <summary>
/// Aba Embalagens: cadastro de embalagem (GLT, caixa) com várias etiquetas, leitura das etiquetas pelo leitor e lista
/// com status/última detecção por embalagem. A captura é delegada ao <see cref="MainWindowViewModel"/>, dono do pipeline;
/// <c>changed</c> avisa que o mapa EPC → embalagem da passagem precisa ser recarregado.
/// </summary>
public sealed partial class PackageCatalogViewModel : ObservableObject
{
    private const int QueryLimit = 500;

    private readonly IPackageCatalogRepository _packages;
    private readonly Func<Task<TagCaptureResult?>> _capture;
    private readonly Func<Task> _changed;
    private int _loadRequestId;

    public PackageCatalogViewModel(IPackageCatalogRepository packages, Func<Task<TagCaptureResult?>> capture, Func<Task> changed)
    {
        _packages = packages;
        _capture = capture;
        _changed = changed;
        Etiquetas.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EtiquetasTitle));
    }

    // ───────────────────────── Formulário ─────────────────────────

    /// <summary>Id da embalagem em edição; null = nova.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle), nameof(SaveLabel))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private long? _editingId;

    public bool IsEditing => EditingId is not null;
    public string FormTitle => IsEditing ? "EDITAR EMBALAGEM" : "NOVA EMBALAGEM";
    public string SaveLabel => IsEditing ? "Salvar alterações" : "Cadastrar";

    [ObservableProperty] private string? _codigo;
    [ObservableProperty] private string? _descricao;
    [ObservableProperty] private string? _tipoGlt;
    [ObservableProperty] private string? _comprimentoMm;
    [ObservableProperty] private string? _larguraMm;
    [ObservableProperty] private string? _alturaMm;
    [ObservableProperty] private string? _pesoKg;
    [ObservableProperty] private string? _observacao;
    [ObservableProperty] private string? _formMessage;
    [ObservableProperty] private bool _formMessageIsError;

    public ObservableCollection<PackageTagItem> Etiquetas { get; } = [];

    public string EtiquetasTitle => $"ETIQUETAS * ({Etiquetas.Count}/{PackageRules.MaxEtiquetas})";

    [ObservableProperty] private string? _newEpc;
    [ObservableProperty] private string? _newPosicao;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand), nameof(CaptureCommand))]
    private bool _isWorking;

    /// <summary>Tags vistas na última leitura que ainda não estão na lista de etiquetas.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCapturedTags))]
    private IReadOnlyList<CapturedTagItem> _capturedTags = [];

    public bool HasCapturedTags => CapturedTags.Count > 0;
    [ObservableProperty] private CapturedTagItem? _selectedCapturedTag;

    private bool CanWork() => !IsWorking;

    [RelayCommand]
    private void AddTag()
    {
        if (!TagRegistrationRules.TryValidateEpc(NewEpc, out var epc, out var error))
        {
            SetMessage(error, isError: true);
            return;
        }
        if (TryAddEtiqueta(epc, NewPosicao))
        {
            NewEpc = null;
            NewPosicao = null;
            FormMessage = null;
        }
    }

    [RelayCommand]
    private void RemoveTag(PackageTagItem? item)
    {
        if (item is not null)
            Etiquetas.Remove(item);
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task CaptureAsync()
    {
        IsWorking = true;
        CapturedTags = [];
        SetMessage("Lendo... aproxime a embalagem da antena (gire para pegar as duas faces).", isError: false);
        try
        {
            var result = await _capture();
            if (result is null)
                return; // o delegate já explicou o motivo via ReportCaptureUnavailable

            if (result.Tags.Count == 0)
            {
                SetMessage("Nenhuma tag lida. Aproxime a etiqueta e tente de novo (confira RSSI mínimo e antenas na Configuração).", isError: true);
                return;
            }

            var fresh = result.Tags.Where(t => Etiquetas.All(e => e.Epc != t.Epc)).ToArray();
            if (fresh.Length == 0)
            {
                SetMessage("As tags lidas já estão na lista.", isError: false);
                return;
            }

            if (result.Tags.Count == 1)
            {
                TryAddEtiqueta(fresh[0].Epc, null);
                SetMessage($"Etiqueta {fresh[0].Epc} adicionada. Leia a outra face ou clique em {SaveLabel}.", isError: false);
                return;
            }

            // Mais de uma tag no campo: pode ser a outra face ou a caixa vizinha. O operador escolhe.
            CapturedTags = fresh.Select(t => new CapturedTagItem(t)).ToArray();
            SetMessage($"{result.Tags.Count} tags no campo. Clique nas que pertencem a esta embalagem (maior RSSI primeiro).", isError: false);
        }
        catch (Exception ex)
        {
            SetMessage($"Falha na leitura: {ex.Message}", isError: true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    partial void OnSelectedCapturedTagChanged(CapturedTagItem? value)
    {
        if (value is null)
            return;
        TryAddEtiqueta(value.Epc, null);
        CapturedTags = CapturedTags.Where(t => t.Epc != value.Epc).ToArray();
    }

    /// <summary>Usado pelo delegate de captura quando ela não pode rodar (modo ativo, leitor parado).</summary>
    public void ReportCaptureUnavailable(string reason) => SetMessage(reason, isError: true);

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SaveAsync()
    {
        var input = BuildInput();
        if (!PackageRules.TryValidate(input, out var valid, out var error))
        {
            SetMessage(error, isError: true);
            return;
        }

        IsWorking = true;
        try
        {
            var editing = EditingId;
            var outcome = editing is { } id
                ? await Task.Run(() => _packages.UpdatePackageAsync(id, input))
                : await Task.Run(() => _packages.AddPackageAsync(input));
            SetMessage(outcome.Message, isError: !outcome.Succeeded);
            if (!outcome.Succeeded)
                return;

            await NotifyChangedAsync();
            ClearForm(keepMessage: true);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetMessage($"Erro ao gravar {valid!.Codigo}: {ex.Message}", isError: true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    private bool CanDelete() => !IsWorking && IsEditing;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        if (EditingId is not { } id)
            return;

        var codigo = Codigo;
        IsWorking = true;
        try
        {
            var removed = await Task.Run(() => _packages.DeletePackageAsync(id));
            SetMessage(removed
                ? $"Embalagem {codigo} removida. As etiquetas voltam a ser tags avulsas; histórico de movimentações mantido."
                : $"{codigo} já não estava cadastrada.", isError: !removed);
            await NotifyChangedAsync();
            ClearForm(keepMessage: true);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetMessage($"Erro ao remover: {ex.Message}", isError: true);
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private void New()
    {
        SelectedPackage = null;
        ClearForm(keepMessage: false);
    }

    /// <summary>Abre a embalagem da etiqueta (vindo da aba Consulta). False = EPC não é etiqueta de embalagem.</summary>
    public async Task<bool> OpenByEpcAsync(string epc)
    {
        var package = await Task.Run(() => _packages.FindPackageByEpcAsync(epc));
        if (package is null)
            return false;
        SelectedPackage = null;
        LoadForm(package);
        SetMessage($"Etiqueta {epc} pertence a {package.Cadastro.Codigo}: aberta para edição.", isError: false);
        return true;
    }

    // ───────────────────────── Lista ─────────────────────────

    public string[] StatusFilterOptions { get; } = ["Todos", "DENTRO", "FORA", "NUNCA LIDA"];

    [ObservableProperty] private int _statusFilterIndex;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private IReadOnlyList<PackageRowItem> _packagesList = [];
    [ObservableProperty] private PackageRowItem? _selectedPackage;
    [ObservableProperty] private string _listMessage = "";

    [RelayCommand]
    private Task SearchAsync() => LoadAsync();

    partial void OnSelectedPackageChanged(PackageRowItem? value)
    {
        if (value is null)
            return;
        LoadForm(value.Package);
        FormMessage = null;
    }

    public async Task LoadAsync()
    {
        var requestId = ++_loadRequestId;
        var term = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var status = StatusFilterIndex switch
        {
            1 => RegisteredTagStatusFilter.Dentro,
            2 => RegisteredTagStatusFilter.Fora,
            3 => RegisteredTagStatusFilter.NuncaLida,
            _ => RegisteredTagStatusFilter.Todos,
        };
        ListMessage = "Carregando...";
        try
        {
            var rows = await Task.Run(() => _packages.QueryPackagesAsync(term, status, QueryLimit));
            if (requestId != _loadRequestId)
                return;

            PackagesList = rows.Select(r => new PackageRowItem(r)).ToArray();
            SelectedPackage = null;
            var never = rows.Count(r => r.NeverDetected);
            ListMessage = rows.Count >= QueryLimit
                ? $"{rows.Count} embalagens (limite atingido — refine a busca)"
                : never > 0 ? $"{rows.Count} · {never} nunca lida(s)" : $"{rows.Count} embalagem(ns)";
        }
        catch (Exception ex)
        {
            if (requestId == _loadRequestId)
                ListMessage = $"Erro ao carregar embalagens: {ex.Message}";
        }
    }

    // ───────────────────────── Auxiliares ─────────────────────────

    private bool TryAddEtiqueta(string epc, string? posicao)
    {
        if (Etiquetas.Any(e => e.Epc == epc))
        {
            SetMessage($"EPC {epc} já está na lista.", isError: true);
            return false;
        }
        if (Etiquetas.Count >= PackageRules.MaxEtiquetas)
        {
            SetMessage($"Máximo de {PackageRules.MaxEtiquetas} etiquetas por embalagem.", isError: true);
            return false;
        }
        Etiquetas.Add(new PackageTagItem(epc, string.IsNullOrWhiteSpace(posicao) ? null : posicao.Trim().ToUpperInvariant()));
        return true;
    }

    private PackageInput BuildInput() => new(
        Codigo, Descricao, TipoGlt, ComprimentoMm, LarguraMm, AlturaMm, PesoKg, Observacao,
        Etiquetas.Select(e => new PackageTagInput(e.Epc, e.Posicao)).ToArray());

    private async Task NotifyChangedAsync()
    {
        try
        {
            await _changed();
        }
        catch (Exception ex)
        {
            SetMessage($"{FormMessage} Atenção: mapa de etiquetas da passagem não recarregado ({ex.Message}).", isError: true);
        }
    }

    private void LoadForm(RegisteredPackage package)
    {
        var c = package.Cadastro;
        EditingId = c.Id;
        Codigo = c.Codigo;
        Descricao = c.Descricao;
        TipoGlt = c.TipoGlt;
        ComprimentoMm = c.ComprimentoMm?.ToString(CultureInfo.InvariantCulture);
        LarguraMm = c.LarguraMm?.ToString(CultureInfo.InvariantCulture);
        AlturaMm = c.AlturaMm?.ToString(CultureInfo.InvariantCulture);
        PesoKg = c.PesoKg?.ToString("0.###", CultureInfo.CurrentCulture);
        Observacao = c.Observacao;
        Etiquetas.Clear();
        foreach (var tag in c.Etiquetas)
            Etiquetas.Add(new PackageTagItem(tag.Epc, tag.Posicao));
        CapturedTags = [];
    }

    private void ClearForm(bool keepMessage)
    {
        EditingId = null;
        Codigo = null;
        Descricao = null;
        TipoGlt = null;
        ComprimentoMm = null;
        LarguraMm = null;
        AlturaMm = null;
        PesoKg = null;
        Observacao = null;
        NewEpc = null;
        NewPosicao = null;
        Etiquetas.Clear();
        CapturedTags = [];
        if (!keepMessage)
            FormMessage = null;
    }

    private void SetMessage(string message, bool isError)
    {
        FormMessage = message;
        FormMessageIsError = isError;
    }
}

/// <summary>Etiqueta na lista do formulário.</summary>
public sealed class PackageTagItem(string epc, string? posicao)
{
    public string Epc { get; } = epc;
    public string? Posicao { get; } = posicao;
    public string PosicaoText { get; } = posicao ?? "—";
}

/// <summary>Linha da lista de embalagens: cadastro + status + última detecção (horário local).</summary>
public sealed class PackageRowItem(RegisteredPackage package)
{
    public RegisteredPackage Package { get; } = package;
    public string Codigo { get; } = package.Cadastro.Codigo;
    public string Descricao { get; } = package.Cadastro.Descricao ?? "—";
    public string Tipo { get; } = package.Cadastro.TipoGlt ?? "—";
    public string Medidas { get; } = FormatDimensions(package.Cadastro);
    public string Peso { get; } = package.Cadastro.PesoKg is { } kg ? $"{kg.ToString("0.###", CultureInfo.CurrentCulture)} kg" : "";
    public string Etiquetas { get; } = package.Cadastro.Etiquetas.Count.ToString(CultureInfo.CurrentCulture);
    public string Status { get; } = package.Status is { } s ? Display.Status(s) : "NUNCA LIDA";
    public bool IsDentro { get; } = package.Status == TagStatus.Dentro;
    public bool IsNeverDetected { get; } = package.NeverDetected;
    public string UltimaDeteccao { get; } = package.UltimaDeteccaoUtc is { } t ? Display.LocalTime(t, "dd/MM/yyyy HH:mm:ss") : "—";
    public string UltimaDeteccaoDetalhe { get; } = package.UltimaEpc is { } epc
        ? $"…{epc[Math.Max(0, epc.Length - 8)..]} · ANT {package.UltimaAntenaId?.ToString(CultureInfo.CurrentCulture) ?? "—"}" +
          (package.UltimaRssi is { } r ? $" · {Display.Rssi(r)} dBm" : "")
        : "";

    private static string FormatDimensions(PackageRegistration c) =>
        c.ComprimentoMm is null && c.LarguraMm is null && c.AlturaMm is null
            ? "—"
            : $"{Mm(c.ComprimentoMm)}×{Mm(c.LarguraMm)}×{Mm(c.AlturaMm)} mm";

    private static string Mm(int? value) => value?.ToString(CultureInfo.CurrentCulture) ?? "?";
}
