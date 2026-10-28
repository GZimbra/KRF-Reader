using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Domain;

namespace KrfReader.UI.ViewModels;

/// <summary>
/// Aba Cadastro: inclusão/edição/remoção de tags em <c>TagCadastro</c>, captura do EPC pelo leitor e
/// lista das tags cadastradas com status atual e última detecção.
/// A captura é delegada ao <see cref="MainWindowViewModel"/>, dono do ciclo de vida do pipeline.
/// </summary>
public sealed partial class TagCatalogViewModel : ObservableObject
{
    private const int QueryLimit = 500;

    private readonly ITagCatalogRepository _catalog;
    private readonly Func<Task<TagCaptureResult?>> _capture;
    private int _loadRequestId;

    public TagCatalogViewModel(ITagCatalogRepository catalog, Func<Task<TagCaptureResult?>> capture)
    {
        _catalog = catalog;
        _capture = capture;
    }

    // ───────────────────────── Formulário ─────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormTitle), nameof(SaveLabel))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _isEditing;

    public string FormTitle => IsEditing ? "EDITAR TAG" : "NOVA TAG";
    public string SaveLabel => IsEditing ? "Salvar alterações" : "Cadastrar";

    [ObservableProperty] private string? _epc;
    [ObservableProperty] private string? _descricao;
    [ObservableProperty] private string? _sku;
    [ObservableProperty] private string? _observacao;
    [ObservableProperty] private string? _categoria;
    [ObservableProperty] private string? _unidade;
    [ObservableProperty] private string? _localizacao;
    [ObservableProperty] private string? _formMessage;
    [ObservableProperty] private bool _formMessageIsError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand), nameof(CaptureCommand))]
    private bool _isWorking;

    /// <summary>Tags vistas na última captura, quando havia mais de uma no campo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCapturedTags))]
    private IReadOnlyList<CapturedTagItem> _capturedTags = [];

    public bool HasCapturedTags => CapturedTags.Count > 0;
    [ObservableProperty] private CapturedTagItem? _selectedCapturedTag;

    private bool CanWork() => !IsWorking;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task SaveAsync()
    {
        var input = new TagRegistrationInput(Epc, Descricao, Sku, Observacao, Categoria, Unidade, Localizacao);
        if (!TagRegistrationRules.TryValidate(input, out var valid, out var error))
        {
            SetMessage(error, isError: true);
            return;
        }

        IsWorking = true;
        try
        {
            if (IsEditing)
            {
                var updated = await Task.Run(() => _catalog.UpdateRegistrationAsync(input));
                if (!updated)
                {
                    SetMessage($"A tag {valid!.Epc} não está mais cadastrada (removida por outra estação?).", isError: true);
                    return;
                }
                SetMessage($"Tag {valid!.Epc} atualizada.", isError: false);
            }
            else
            {
                var added = await Task.Run(() => _catalog.AddRegistrationAsync(input));
                if (!added)
                {
                    SetMessage($"EPC {valid!.Epc} já cadastrado. Selecione-o na lista para editar.", isError: true);
                    return;
                }
                SetMessage($"Tag {valid!.Epc} cadastrada.", isError: false);
            }

            // Formulário limpo para o próximo cadastro (fluxo de cadastro em sequência).
            ClearForm(keepMessage: true);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetMessage($"Erro ao gravar: {ex.Message}", isError: true);
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
        var epc = Epc;
        if (string.IsNullOrWhiteSpace(epc))
            return;

        IsWorking = true;
        try
        {
            var removed = await Task.Run(() => _catalog.DeleteRegistrationAsync(epc));
            SetMessage(removed
                ? $"Cadastro de {epc} removido. Estado e histórico de leituras da tag foram mantidos."
                : $"{epc} já não estava cadastrada.", isError: !removed);
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
        SelectedRegistration = null;
        ClearForm(keepMessage: false);
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task CaptureAsync()
    {
        IsWorking = true;
        CapturedTags = [];
        SetMessage("Lendo... aproxime só a tag a cadastrar da antena.", isError: false);
        try
        {
            var result = await _capture();
            if (result is null)
                return; // o delegate já explicou o motivo em FormMessage via ReportCaptureUnavailable

            if (result.Strongest is not { } strongest)
            {
                SetMessage("Nenhuma tag lida. Aproxime a tag e tente de novo (confira RSSI mínimo e antenas na Configuração).", isError: true);
                return;
            }

            await OpenEpcAsync(strongest.Epc);
            if (result.IsAmbiguous)
            {
                CapturedTags = result.Tags.Select(t => new CapturedTagItem(t)).ToArray();
                SetMessage($"{result.Tags.Count} tags no campo. Usado o EPC de maior RSSI; confira ou escolha outro na lista sob o campo EPC.", isError: true);
            }
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
        if (value is not null)
            _ = OpenEpcAsync(value.Epc);
    }

    /// <summary>Usado pelo delegate de captura quando ela não pode rodar (modo ativo, leitor parado).</summary>
    public void ReportCaptureUnavailable(string reason) => SetMessage(reason, isError: true);

    /// <summary>EPC já cadastrado abre em edição; senão, vira novo cadastro com o EPC preenchido.</summary>
    public async Task OpenEpcAsync(string epc)
    {
        SelectedRegistration = null;
        var existing = await Task.Run(() => _catalog.GetRegistrationAsync(epc));
        if (existing is not null)
        {
            LoadForm(existing);
            SetMessage($"EPC {epc} já cadastrado: aberto para edição.", isError: false);
        }
        else
        {
            IsEditing = false;
            Epc = epc;
            Descricao = null;
            Sku = null;
            Observacao = null;
            Categoria = null;
            Unidade = null;
            Localizacao = null;
            SetMessage($"EPC {epc} lido. Preencha a descrição e clique em Cadastrar.", isError: false);
        }
    }

    // ───────────────────────── Lista ─────────────────────────

    public string[] StatusFilterOptions { get; } = ["Todos", "DENTRO", "FORA", "NUNCA LIDA"];

    [ObservableProperty] private int _statusFilterIndex;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private IReadOnlyList<TagRegistrationItem> _registrations = [];
    [ObservableProperty] private TagRegistrationItem? _selectedRegistration;
    [ObservableProperty] private string _listMessage = "";

    [RelayCommand]
    private Task SearchAsync() => LoadAsync();

    partial void OnSelectedRegistrationChanged(TagRegistrationItem? value)
    {
        if (value is null)
            return;
        CapturedTags = [];
        LoadForm(value.Registration);
        FormMessage = null;
    }

    /// <summary>Recarrega a lista. Chamado na abertura da aba e após gravar.</summary>
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
            var rows = await Task.Run(() => _catalog.QueryRegisteredTagsAsync(term, status, QueryLimit));
            if (requestId != _loadRequestId)
                return;

            var items = rows.Select(r => new TagRegistrationItem(r)).ToArray();
            Registrations = items;
            SelectedRegistration = null;
            var never = rows.Count(r => r.NeverDetected);
            ListMessage = rows.Count >= QueryLimit
                ? $"{rows.Count} tags cadastradas (limite atingido — refine a busca)"
                : never > 0 ? $"{rows.Count} tag(s) · {never} nunca lida(s)" : $"{rows.Count} tag(s)";
        }
        catch (Exception ex)
        {
            if (requestId == _loadRequestId)
                ListMessage = $"Erro ao carregar cadastro: {ex.Message}";
        }
    }

    private void LoadForm(TagRegistration registration)
    {
        IsEditing = true;
        Epc = registration.Epc;
        Descricao = registration.Descricao;
        Sku = registration.Sku;
        Observacao = registration.Observacao;
        Categoria = registration.Categoria;
        Unidade = registration.Unidade;
        Localizacao = registration.Localizacao;
    }

    private void ClearForm(bool keepMessage)
    {
        IsEditing = false;
        Epc = null;
        Descricao = null;
        Sku = null;
        Observacao = null;
        Categoria = null;
        Unidade = null;
        Localizacao = null;
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

/// <summary>Linha da lista de tags cadastradas: cadastro + status + última detecção (horário local).</summary>
public sealed class TagRegistrationItem(RegisteredTag tag)
{
    public TagRegistration Registration { get; } = tag.Cadastro;
    public string Epc { get; } = tag.Epc;
    public string Descricao { get; } = tag.Cadastro.Descricao;
    public string Sku { get; } = tag.Cadastro.Sku ?? "—";
    public string Local { get; } = tag.Cadastro.Localizacao ?? "—";
    public string Status { get; } = tag.Status is { } s ? Display.Status(s) : "NUNCA LIDA";
    public bool IsDentro { get; } = tag.Status == TagStatus.Dentro;
    public bool IsNeverDetected { get; } = tag.NeverDetected;
    public string UltimaDeteccao { get; } = tag.UltimaDeteccaoUtc is { } t ? Display.LocalTime(t, "dd/MM/yyyy HH:mm:ss") : "—";
    public string UltimaDeteccaoDetalhe { get; } = tag.UltimaAntenaId is { } a
        ? $"ANT {a} · {(tag.UltimaRssi is { } r ? Display.Rssi(r) + " dBm" : "—")}"
        : "";
}

public sealed class CapturedTagItem(CapturedTag tag)
{
    public string Epc { get; } = tag.Epc;
    public string Detail { get; } = $"RSSI {Display.Rssi(tag.MaxRssi)} · {tag.Reads} leitura(s) · antena {tag.AntennaId}";
}
