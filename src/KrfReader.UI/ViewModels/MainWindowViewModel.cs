using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Application.Readers;
using KrfReader.Domain;

namespace KrfReader.UI.ViewModels;

/// <summary>
/// ViewModel da janela principal. Depende só de interfaces; o pipeline concreto vem de <c>pipelineFactory</c>.
/// <para>
/// Threading: eventos do pipeline chegam em threads de background. Leituras vão para uma ConcurrentQueue
/// drenada por um DispatcherTimer (250 ms); demais eventos fazem Post para a UI thread. Toda chamada a
/// pipeline/repositório roda via Task.Run (Microsoft.Data.Sqlite e SerialPort.Open são síncronos por baixo).
/// </para>
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private const int MaxLiveItems = 200;
    private const int MaxPendingLiveItems = 2_000;
    private static readonly char[] AntennaSeparators = [',', ';', ' '];
    private static readonly TimeSpan CountersMinInterval = TimeSpan.FromSeconds(1);

    private readonly IInventoryRepository _repository;
    private readonly Func<ReaderOptions, PipelineHandle> _pipelineFactory;
    private readonly ReaderOptions _baseReaderOptions;
    private readonly PipelineOptions _pipelineOptions;
    private readonly ConcurrentQueue<LiveReadItem> _pendingLiveReads = new();
    private readonly DispatcherTimer _uiTimer;

    // Serializa init / troca de modo / troca de leitor / shutdown.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    private PipelineHandle? _current;
    private bool _repositoryReady;
    private int _countersDirty;
    private bool _countersLoading;
    private DateTime _lastCountersReloadUtc = DateTime.MinValue;
    private bool _shutdownStarted;
    private int _historyRequestId;

    public MainWindowViewModel(
        IInventoryRepository repository,
        IReaderProtocolFactory protocolFactory,
        Func<ReaderOptions, PipelineHandle> pipelineFactory,
        ReaderOptions readerOptions,
        PipelineOptions pipelineOptions)
    {
        _repository = repository;
        _pipelineFactory = pipelineFactory;
        _baseReaderOptions = readerOptions;
        _pipelineOptions = pipelineOptions;

        Protocols = protocolFactory.AvailableProtocols.ToArray();

        _readerTypeIndex = (int)readerOptions.Type;
        _selectedProtocol = Protocols.Contains(readerOptions.Protocol) ? readerOptions.Protocol : Protocols.FirstOrDefault();
        _serialPortName = readerOptions.Serial.PortName;
        _baudRate = readerOptions.Serial.BaudRate;
        _tcpHost = readerOptions.Tcp.Host;
        _tcpPort = readerOptions.Tcp.Port;
        _powerDbm = (decimal)Math.Clamp(readerOptions.PowerDbm, 0, 33);
        _readerAntennasText = string.Join(",", readerOptions.Antennas);

        _minRssi = Math.Clamp(pipelineOptions.MinRssi, -100, -20);
        _filterAntennasText = string.Join(",", pipelineOptions.AllowedAntennas);
        _filterMessage = DescribeFilter(pipelineOptions.AllowedAntennas);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += OnUiTick;
    }

    // ───────────────────────── Operação ─────────────────────────

    public ObservableCollection<LiveReadItem> LiveReads { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeBannerText), nameof(ModeBannerHint), nameof(IsEntradaActive),
        nameof(IsSaidaActive), nameof(IsStopped), nameof(IsModeActive))]
    private OperationMode? _activeMode;

    public string ModeBannerText => ActiveMode switch
    {
        OperationMode.Entrada => "MODO ENTRADA",
        OperationMode.Saida => "MODO SAÍDA",
        _ => "PARADO",
    };

    public string ModeBannerHint => ActiveMode switch
    {
        OperationMode.Entrada => "Tags lidas agora são marcadas como DENTRO do estoque",
        OperationMode.Saida => "Tags lidas agora são marcadas como FORA do estoque",
        _ => "Nenhuma leitura está sendo aplicada ao estoque",
    };

    public bool IsEntradaActive => ActiveMode == OperationMode.Entrada;
    public bool IsSaidaActive => ActiveMode == OperationMode.Saida;
    public bool IsStopped => ActiveMode is null;
    public bool IsModeActive => ActiveMode is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EntradaCommand), nameof(SaidaCommand), nameof(PararCommand), nameof(ApplyReaderCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EntradaCommand), nameof(SaidaCommand), nameof(PararCommand))]
    private bool _isPipelineRunning;

    [ObservableProperty] private bool _isShuttingDown;
    [ObservableProperty] private int _countDentro;
    [ObservableProperty] private int _countFora;
    [ObservableProperty] private int _countAnomalias;
    [ObservableProperty] private int _sessionDistinct;
    [ObservableProperty] private string _techStatsText = "Brutas 0 · filtradas 0 · duplicadas 0 · descartadas 0 · persistidas 0 · drops de fila 0";

    [ObservableProperty] private string _readerName = "—";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _connectionText = "Desconectado";
    [ObservableProperty] private string _statusMessage = "Inicializando...";
    [ObservableProperty] private string? _lastError;

    private bool CanOperate() => !IsBusy && IsPipelineRunning && !_shutdownStarted;

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task EntradaAsync() => RunModeCommandAsync(p => p.SetModeAsync(OperationMode.Entrada), "iniciar ENTRADA");

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task SaidaAsync() => RunModeCommandAsync(p => p.SetModeAsync(OperationMode.Saida), "iniciar SAÍDA");

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task PararAsync() => RunModeCommandAsync(p => p.StopInventoryAsync(), "parar");

    [RelayCommand]
    private void ClearLiveReads() => LiveReads.Clear();

    private async Task RunModeCommandAsync(Func<IInventoryPipeline, Task> action, string description)
    {
        if (!await _lifecycleLock.WaitAsync(0))
            return;
        IsBusy = true;
        try
        {
            var handle = _current;
            if (handle is null || _shutdownStarted)
                return;
            await Task.Run(() => action(handle.Pipeline));
            ActiveMode = handle.Pipeline.CurrentMode;
            StatusMessage = ActiveMode is null ? "Inventário parado." : $"{Display.Mode(ActiveMode)} ativo.";
        }
        catch (Exception ex)
        {
            ReportError($"Falha ao {description}: {ex.Message}");
            if (_current is { } h)
                ActiveMode = h.Pipeline.CurrentMode;
        }
        finally
        {
            IsBusy = false;
            _lifecycleLock.Release();
        }
    }

    // ───────────────────────── Consulta ─────────────────────────

    public string[] StatusFilterOptions { get; } = ["Todos", "DENTRO", "FORA"];

    [ObservableProperty] private int _statusFilterIndex;
    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private IReadOnlyList<TagRowItem> _tags = [];
    [ObservableProperty] private TagRowItem? _selectedTag;
    [ObservableProperty] private IReadOnlyList<EventRowItem> _events = [];
    [ObservableProperty] private string _queryMessage = "Use Buscar para listar as tags.";
    [ObservableProperty] private string _historyTitle = "Selecione uma tag para ver o histórico.";

    [RelayCommand]
    private async Task SearchAsync()
    {
        TagStatus? status = StatusFilterIndex switch
        {
            1 => TagStatus.Dentro,
            2 => TagStatus.Fora,
            _ => null,
        };
        var text = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        const int limit = 500;

        QueryMessage = "Buscando...";
        try
        {
            var result = await Task.Run(() => _repository.QueryTagsAsync(status, text, limit));
            SelectedTag = null;
            Tags = result.Select(t => new TagRowItem(t)).ToArray();
            QueryMessage = result.Count >= limit
                ? $"{result.Count} tags (limite atingido — refine a busca)"
                : $"{result.Count} tag(s) encontrada(s)";
        }
        catch (Exception ex)
        {
            QueryMessage = $"Erro na consulta: {ex.Message}";
        }
    }

    partial void OnSelectedTagChanged(TagRowItem? value) => _ = LoadHistoryAsync(value);

    private async Task LoadHistoryAsync(TagRowItem? tag)
    {
        var requestId = ++_historyRequestId;
        if (tag is null)
        {
            Events = [];
            HistoryTitle = "Selecione uma tag para ver o histórico.";
            return;
        }

        HistoryTitle = $"Histórico de {tag.Epc}: carregando...";
        try
        {
            var events = await Task.Run(() => _repository.GetEventsAsync(tag.Epc));
            if (requestId != _historyRequestId)
                return;
            Events = events.Select(e => new EventRowItem(e)).ToArray();
            HistoryTitle = $"Histórico de {tag.Epc} ({events.Count} evento(s), mais recente primeiro)";
        }
        catch (Exception ex)
        {
            if (requestId == _historyRequestId)
                HistoryTitle = $"Erro ao carregar histórico: {ex.Message}";
        }
    }

    // ───────────────────────── Configuração ─────────────────────────

    public string[] ReaderTypes { get; } = ["Mock (simulado)", "Serial", "TCP/IP"];
    public IReadOnlyList<string> Protocols { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerialSelected), nameof(IsTcpSelected), nameof(IsProtocolRelevant))]
    private int _readerTypeIndex;

    public bool IsSerialSelected => ReaderTypeIndex == (int)ReaderType.Serial;
    public bool IsTcpSelected => ReaderTypeIndex == (int)ReaderType.Tcp;
    public bool IsProtocolRelevant => IsSerialSelected || IsTcpSelected;

    [ObservableProperty] private string? _selectedProtocol;
    [ObservableProperty] private string? _serialPortName;
    [ObservableProperty] private decimal? _baudRate;
    [ObservableProperty] private string? _tcpHost;
    [ObservableProperty] private decimal? _tcpPort;
    [ObservableProperty] private decimal? _powerDbm;
    [ObservableProperty] private string? _readerAntennasText;
    [ObservableProperty] private string? _configMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinRssiText))]
    private double _minRssi;

    public string MinRssiText => $"{MinRssi:0} dBm";

    [ObservableProperty] private string? _filterAntennasText;
    [ObservableProperty] private string _filterMessage;

    private bool CanApplyReader() => !IsBusy && !_shutdownStarted;

    [RelayCommand(CanExecute = nameof(CanApplyReader))]
    private async Task ApplyReaderAsync()
    {
        if (ActiveMode is not null || _current?.Pipeline.CurrentMode is not null)
        {
            ConfigMessage = "Há um modo ativo. Pressione PARAR na aba Operação antes de trocar o leitor.";
            return;
        }

        if (!TryBuildReaderOptions(out var options, out var error))
        {
            ConfigMessage = error;
            return;
        }

        if (!await _lifecycleLock.WaitAsync(0))
            return;
        IsBusy = true;
        try
        {
            ConfigMessage = "Parando o leitor atual (gravando pendências)...";
            await StopCurrentPipelineAsync();

            if (!_repositoryReady && !await TryInitializeRepositoryAsync())
            {
                ConfigMessage = "Banco de dados indisponível; leitor não iniciado. Veja a barra de status.";
                return;
            }

            ConfigMessage = "Conectando o novo leitor...";
            await StartPipelineAsync(options);
            ConfigMessage = IsPipelineRunning
                ? $"Leitor '{ReaderName}' aplicado e conectado."
                : "O leitor não iniciou. Corrija os parâmetros e aplique de novo (detalhes na barra de status).";
        }
        finally
        {
            IsBusy = false;
            _lifecycleLock.Release();
        }
    }

    partial void OnMinRssiChanged(double value) => ApplyFilter();

    partial void OnFilterAntennasTextChanged(string? value) => ApplyFilter();

    private void ApplyFilter()
    {
        if (!TryParseAntennas(FilterAntennasText, allowEmpty: true, out var antennas))
        {
            FilterMessage = "Antenas inválidas: use números separados por vírgula (ex.: 1,2). Filtro anterior mantido.";
            return;
        }

        try
        {
            _current?.Pipeline.UpdateFilter(MinRssi, antennas);
            FilterMessage = DescribeFilter(antennas);
        }
        catch (Exception ex)
        {
            FilterMessage = $"Erro ao aplicar filtro: {ex.Message}";
        }
    }

    private static string DescribeFilter(IReadOnlyCollection<int> antennas) =>
        antennas.Count == 0 ? "Aceitando todas as antenas." : $"Aceitando apenas antenas {string.Join(", ", antennas)}.";

    private bool TryBuildReaderOptions(out ReaderOptions options, out string error)
    {
        options = null!;
        var type = (ReaderType)Math.Clamp(ReaderTypeIndex, 0, 2);

        if (PowerDbm is not { } power || power < 0 || power > 33)
        {
            error = "Potência deve estar entre 0 e 33 dBm.";
            return false;
        }

        if (!TryParseAntennas(ReaderAntennasText, allowEmpty: false, out var antennas))
        {
            error = "Antenas ativas inválidas: informe ao menos uma, separadas por vírgula (ex.: 1,2).";
            return false;
        }

        var baseSerial = _baseReaderOptions.Serial;
        var baseTcp = _baseReaderOptions.Tcp;
        var serial = new SerialReaderOptions
        {
            PortName = SerialPortName?.Trim() ?? "",
            BaudRate = (int)(BaudRate ?? 0),
            DataBits = baseSerial.DataBits,
            Parity = baseSerial.Parity,
            StopBits = baseSerial.StopBits,
        };
        var tcp = new TcpReaderOptions
        {
            Host = TcpHost?.Trim() ?? "",
            Port = (int)(TcpPort ?? 0),
            ConnectTimeoutMs = baseTcp.ConnectTimeoutMs,
        };

        if (type == ReaderType.Serial)
        {
            if (serial.PortName.Length == 0)
            {
                error = "Informe a porta serial (ex.: COM3 ou /dev/ttyUSB0).";
                return false;
            }
            if (serial.BaudRate <= 0)
            {
                error = "Baud rate inválido.";
                return false;
            }
        }
        else if (type == ReaderType.Tcp)
        {
            if (tcp.Host.Length == 0)
            {
                error = "Informe o host/IP do leitor.";
                return false;
            }
            if (tcp.Port is < 1 or > 65535)
            {
                error = "Porta TCP deve estar entre 1 e 65535.";
                return false;
            }
        }

        if (type != ReaderType.Mock && string.IsNullOrWhiteSpace(SelectedProtocol))
        {
            error = "Selecione um protocolo.";
            return false;
        }

        options = new ReaderOptions
        {
            Type = type,
            Protocol = SelectedProtocol ?? _baseReaderOptions.Protocol,
            Serial = serial,
            Tcp = tcp,
            Mock = _baseReaderOptions.Mock,
            PowerDbm = (double)power,
            Antennas = antennas,
            ReconnectDelayMs = _baseReaderOptions.ReconnectDelayMs,
        };
        error = "";
        return true;
    }

    private static bool TryParseAntennas(string? text, bool allowEmpty, out int[] antennas)
    {
        antennas = [];
        var parts = (text ?? "").Split(AntennaSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new SortedSet<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1 || n > 64)
                return false;
            result.Add(n);
        }

        if (result.Count == 0 && !allowEmpty)
            return false;
        antennas = result.ToArray();
        return true;
    }

    // ───────────────────────── Ciclo de vida ─────────────────────────

    public bool IsShutdownComplete { get; private set; }

    private Task? _initTask;

    /// <summary>Inicializa banco e leitor configurado. Nunca lança: falhas vão para a barra de status.</summary>
    public Task InitializeAsync() => _initTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        _uiTimer.Start();
        await _lifecycleLock.WaitAsync();
        IsBusy = true;
        try
        {
            if (_shutdownStarted)
                return;

            if (!await TryInitializeRepositoryAsync())
                return;

            await ReloadCountersAsync();
            await StartPipelineAsync(_baseReaderOptions);
        }
        catch (Exception ex)
        {
            ReportError($"Falha na inicialização: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _lifecycleLock.Release();
        }
    }

    private async Task<bool> TryInitializeRepositoryAsync()
    {
        StatusMessage = "Abrindo banco de dados...";
        try
        {
            await Task.Run(() => _repository.InitializeAsync());
            _repositoryReady = true;
            return true;
        }
        catch (Exception ex)
        {
            ReportError($"Banco de dados indisponível: {ex.Message}");
            StatusMessage = "Sem banco de dados: leituras não podem ser gravadas.";
            return false;
        }
    }

    /// <summary>Chamado com <see cref="_lifecycleLock"/> adquirido.</summary>
    private async Task StartPipelineAsync(ReaderOptions options)
    {
        if (_shutdownStarted)
            return;

        PipelineHandle handle;
        try
        {
            handle = _pipelineFactory(options);
        }
        catch (Exception ex)
        {
            ReportError($"Não foi possível criar o leitor {options.Type}: {ex.Message}");
            return;
        }

        _current = handle;
        Attach(handle);
        ReaderName = SafeReaderName(handle, options);
        ApplyFilter();

        StatusMessage = $"Conectando a {ReaderName}...";
        try
        {
            await Task.Run(() => handle.Pipeline.StartAsync());
            IsPipelineRunning = true;
            ActiveMode = handle.Pipeline.CurrentMode;
            StatusMessage = "Leitor pronto. Selecione ENTRADA ou SAÍDA.";
        }
        catch (Exception ex)
        {
            IsPipelineRunning = false;
            ReportError($"Falha ao iniciar o leitor {ReaderName}: {ex.Message}");
            StatusMessage = "Leitor não iniciado. Ajuste na aba Configuração e clique em \"Aplicar leitor\".";
        }
    }

    /// <summary>StopAsync (drena e persiste) + Dispose do pipeline e do leitor. Chamado com o lock adquirido.</summary>
    private async Task StopCurrentPipelineAsync()
    {
        var handle = _current;
        if (handle is null)
            return;

        IsPipelineRunning = false;
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _pipelineOptions.ShutdownTimeoutMs) + 5000);
        using (var cts = new CancellationTokenSource(timeout))
        {
            try
            {
                await Task.Run(() => handle.Pipeline.StopAsync(cts.Token));
            }
            catch (Exception ex)
            {
                ReportError($"Erro ao parar o leitor: {ex.Message}");
            }
        }

        Detach(handle);
        try { await Task.Run(async () => await handle.Pipeline.DisposeAsync()); }
        catch (Exception ex) { ReportError($"Erro ao liberar o pipeline: {ex.Message}"); }
        try { await Task.Run(async () => await handle.Reader.DisposeAsync()); }
        catch (Exception ex) { ReportError($"Erro ao liberar o leitor: {ex.Message}"); }

        _current = null;
        ActiveMode = null;
        IsConnected = false;
        ConnectionText = "Desconectado";
    }

    /// <summary>
    /// Shutdown limpo: aguarda init/comandos em andamento, para o pipeline (drena e persiste) e libera tudo.
    /// </summary>
    public async Task ShutdownAsync()
    {
        if (_shutdownStarted)
            return;
        _shutdownStarted = true;
        IsShuttingDown = true;
        StatusMessage = "Encerrando: gravando leituras pendentes...";
        NotifyCommandsCanExecute();

        await _lifecycleLock.WaitAsync();
        IsBusy = true;
        try
        {
            await StopCurrentPipelineAsync();
        }
        finally
        {
            _uiTimer.Stop();
            DrainLiveReads();
            IsShutdownComplete = true;
            _lifecycleLock.Release();
        }
    }

    private void NotifyCommandsCanExecute()
    {
        EntradaCommand.NotifyCanExecuteChanged();
        SaidaCommand.NotifyCanExecuteChanged();
        PararCommand.NotifyCanExecuteChanged();
        ApplyReaderCommand.NotifyCanExecuteChanged();
    }

    private static string SafeReaderName(PipelineHandle handle, ReaderOptions options)
    {
        try { return handle.Reader.Name; }
        catch { return options.Type.ToString(); }
    }

    // ───────────────────────── Eventos do pipeline (threads de background) ─────────────────────────

    private void Attach(PipelineHandle handle)
    {
        handle.Pipeline.BatchPersisted += OnBatchPersisted;
        handle.Pipeline.ModeChanged += OnModeChanged;
        handle.Pipeline.Error += OnPipelineError;
    }

    private void Detach(PipelineHandle handle)
    {
        handle.Pipeline.BatchPersisted -= OnBatchPersisted;
        handle.Pipeline.ModeChanged -= OnModeChanged;
        handle.Pipeline.Error -= OnPipelineError;
    }

    private void OnBatchPersisted(object? sender, BatchResult batch)
    {
        // Só o fim do lote interessa à lista (máx. 200 visíveis).
        var applied = batch.Applied;
        for (var i = Math.Max(0, applied.Count - MaxLiveItems); i < applied.Count; i++)
            _pendingLiveReads.Enqueue(new LiveReadItem(applied[i]));

        while (_pendingLiveReads.Count > MaxPendingLiveItems && _pendingLiveReads.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _countersDirty, 1);
    }

    private void OnModeChanged(object? sender, OperationMode? mode) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, _current?.Pipeline))
                ActiveMode = mode;
        });

    private void OnPipelineError(object? sender, string message) =>
        Dispatcher.UIThread.Post(() => ReportError(message));

    private void ReportError(string message) =>
        LastError = $"{DateTime.Now:HH:mm:ss} {message}";

    // ───────────────────────── Timer de UI ─────────────────────────

    private void OnUiTick(object? sender, EventArgs e)
    {
        DrainLiveReads();

        var handle = _current;
        if (handle is not null)
        {
            try
            {
                var s = handle.Pipeline.GetStats();
                SessionDistinct = s.DistinctEpcsSession;
                TechStatsText =
                    $"Brutas {s.RawReads:N0} · filtradas {s.FilteredOut:N0} · duplicadas {s.Duplicates:N0} · " +
                    $"descartadas {s.StaleDiscarded:N0} · persistidas {s.Persisted:N0} · drops de fila {s.QueueDropped:N0}";

                var connected = handle.Reader.IsConnected;
                IsConnected = connected;
                ConnectionText = connected
                    ? (handle.Reader.IsInventoryRunning ? "Conectado · lendo" : "Conectado")
                    : (IsPipelineRunning ? "Desconectado (reconectando...)" : "Desconectado");
            }
            catch
            {
                // Stats são best-effort; erros reais chegam pelo evento Error.
            }
        }

        if (_repositoryReady
            && !_countersLoading
            && Volatile.Read(ref _countersDirty) == 1
            && DateTime.UtcNow - _lastCountersReloadUtc >= CountersMinInterval)
        {
            _ = ReloadCountersAsync();
        }
    }

    private void DrainLiveReads()
    {
        if (_pendingLiveReads.IsEmpty)
            return;

        var drained = new List<LiveReadItem>();
        while (_pendingLiveReads.TryDequeue(out var item))
            drained.Add(item);

        for (var i = Math.Max(0, drained.Count - MaxLiveItems); i < drained.Count; i++)
            LiveReads.Insert(0, drained[i]);

        while (LiveReads.Count > MaxLiveItems)
            LiveReads.RemoveAt(LiveReads.Count - 1);
    }

    private async Task ReloadCountersAsync()
    {
        _countersLoading = true;
        Interlocked.Exchange(ref _countersDirty, 0);
        try
        {
            var c = await Task.Run(() => _repository.GetCountersAsync());
            CountDentro = c.Dentro;
            CountFora = c.Fora;
            CountAnomalias = c.Anomalias;
        }
        catch (Exception ex)
        {
            ReportError($"Erro ao ler contadores: {ex.Message}");
        }
        finally
        {
            _lastCountersReloadUtc = DateTime.UtcNow;
            _countersLoading = false;
        }
    }
}
