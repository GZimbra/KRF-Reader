using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KrfReader.Application.Persistence;
using KrfReader.Domain;

namespace KrfReader.UI.ViewModels;

/// <summary>Aba Movimentações: consulta de <c>Movimentacoes</c> por período, tipo e EPC/item.</summary>
public sealed partial class StockMovementsViewModel : ObservableObject
{
    private const int QueryLimit = 1000;

    private readonly IInventoryRepository _repository;
    private int _loadRequestId;

    public StockMovementsViewModel(IInventoryRepository repository)
    {
        _repository = repository;
        SetPeriod(days: 7);
    }

    public string[] TipoOptions { get; } = ["Todos", "ENTRADA", "SAÍDA", "ANOMALIA"];

    /// <summary>Datas locais (dia do calendário); o horário é ignorado.</summary>
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private int _tipoIndex;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private IReadOnlyList<MovementRowItem> _movements = [];
    [ObservableProperty] private string _message = "Defina o período e clique em BUSCAR.";
    [ObservableProperty] private bool _messageIsError;
    [ObservableProperty] private int _countEntradas;
    [ObservableProperty] private int _countSaidas;
    [ObservableProperty] private int _countAnomalias;

    [RelayCommand]
    private Task SearchAsync() => LoadAsync();

    [RelayCommand]
    private Task TodayAsync()
    {
        SetPeriod(days: 1);
        return LoadAsync();
    }

    [RelayCommand]
    private Task Last7DaysAsync()
    {
        SetPeriod(days: 7);
        return LoadAsync();
    }

    [RelayCommand]
    private Task Last30DaysAsync()
    {
        SetPeriod(days: 30);
        return LoadAsync();
    }

    public async Task LoadAsync()
    {
        var requestId = ++_loadRequestId;
        if (FromDate is { } f && ToDate is { } t && f.Date > t.Date)
        {
            SetMessage("Período inválido: data inicial depois da final.", isError: true);
            return;
        }

        var (fromUtc, toUtc) = MovementFilter.LocalDaysToUtc(
            FromDate is { } from ? DateOnly.FromDateTime(from) : null,
            ToDate is { } to ? DateOnly.FromDateTime(to) : null,
            TimeZoneInfo.Local);
        var filter = new MovementFilter(
            fromUtc,
            toUtc,
            TipoIndex switch
            {
                1 => OperationMode.Entrada,
                2 => OperationMode.Saida,
                _ => null,
            },
            string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            OnlyAnomalies: TipoIndex == 3,
            Limit: QueryLimit);

        SetMessage("Buscando...", isError: false);
        try
        {
            var rows = await Task.Run(() => _repository.QueryMovementsAsync(filter));
            if (requestId != _loadRequestId)
                return;

            Movements = rows.Select(r => new MovementRowItem(r)).ToArray();
            CountEntradas = rows.Count(r => r.Tipo == OperationMode.Entrada);
            CountSaidas = rows.Count(r => r.Tipo == OperationMode.Saida);
            CountAnomalias = rows.Count(r => r.IsAnomaly);
            SetMessage(rows.Count >= QueryLimit
                ? $"{rows.Count} movimentações (limite atingido: reduza o período)"
                : $"{rows.Count} movimentação(ões)", isError: false);
        }
        catch (Exception ex)
        {
            if (requestId == _loadRequestId)
                SetMessage($"Erro na consulta: {ex.Message}", isError: true);
        }
    }

    private void SetPeriod(int days)
    {
        ToDate = DateTime.Today;
        FromDate = DateTime.Today.AddDays(1 - days);
    }

    private void SetMessage(string message, bool isError)
    {
        Message = message;
        MessageIsError = isError;
    }
}
