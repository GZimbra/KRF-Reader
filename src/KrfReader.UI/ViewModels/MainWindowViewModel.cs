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

    /// <summary>Janela de leitura do botão "Ler tag" do cadastro.</summary>
    private static readonly TimeSpan CaptureWindow = TimeSpan.FromMilliseconds(1500);

    private readonly IInventoryRepository _repository;
    private readonly IPackageCatalogRepository _packages;
    private readonly PackageDirectory _packageDirectory;
    private readonly Func<ReaderOptions, PipelineHandle> _pipelineFactory;
    private readonly Func<ReaderOptions, Task> _saveReaderOptions;
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
        ITagCatalogRepository catalog,
        IPackageCatalogRepository packages,
        PackageDirectory packageDirectory,
        IReaderProtocolFactory protocolFactory,
        IReaderModelCatalog readerModels,
        Func<ReaderOptions, PipelineHandle> pipelineFactory,
        Func<ReaderOptions, Task> saveReaderOptions,
        ReaderOptions readerOptions,
        PipelineOptions pipelineOptions,
        PassageOptions passageOptions,
        DatabaseSettingsViewModel banco)
    {
        _repository = repository;
        _packages = packages;
        _packageDirectory = packageDirectory;
        _pipelineFactory = pipelineFactory;
        _saveReaderOptions = saveReaderOptions;
        _baseReaderOptions = readerOptions;
        _pipelineOptions = pipelineOptions;
        Cadastro = new TagCatalogViewModel(catalog, () => CaptureForCatalogAsync(Cadastro!.ReportCaptureUnavailable));
        Embalagens = new PackageCatalogViewModel(
            packages, () => CaptureForCatalogAsync(Embalagens!.ReportCaptureUnavailable), RefreshPackageDirectoryAsync);
        Movimentacoes = new StockMovementsViewModel(repository);
        Banco = banco;

        Protocols = protocolFactory.AvailableProtocols.ToArray();
        ReaderModels = readerModels.Models;
        // Sem disparar OnSelectedReaderModelChanged: os campos vêm das opções salvas, não dos padrões do modelo.
        _selectedReaderModel = readerModels.Resolve(readerOptions);
        RefreshSerialPorts();

        _readerTypeIndex = (int)readerOptions.Type;
        _selectedProtocol = Protocols.Contains(readerOptions.Protocol) ? readerOptions.Protocol : Protocols.FirstOrDefault();
        _serialPortName = readerOptions.Serial.PortName;
        _baudRate = readerOptions.Serial.BaudRate;
        _tcpHost = readerOptions.Tcp.Host;
        _tcpPort = readerOptions.Tcp.Port;
        _powerDbm = (decimal)Math.Clamp(readerOptions.PowerDbm, 0, 33);
        _readerAntennasText = string.Join(",", readerOptions.Antennas);
        _antennaPowerText = FormatAntennaPower(readerOptions.AntennaPowerDbm);
        _enforceAnatelBand = readerOptions.EnforceAnatelBand;
        _dockEntradaText = string.Join(",", readerOptions.Docks?.EntradaAntennas ?? []);
        _dockSaidaText = string.Join(",", readerOptions.Docks?.SaidaAntennas ?? []);
        _iotUseHttps = readerOptions.ImpinjIot.UseHttps;
        _iotUsername = readerOptions.ImpinjIot.Username;
        _iotPassword = readerOptions.ImpinjIot.Password;

        _minRssi = Math.Clamp(pipelineOptions.MinRssi, -100, -20);
        _filterAntennasText = string.Join(",", pipelineOptions.AllowedAntennas);
        _filterMessage = DescribeFilter(pipelineOptions.AllowedAntennas);
        InitializeInventoryFields(readerOptions, passageOptions);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _uiTimer.Tick += OnUiTick;
    }

    // ───────────────────────── Operação ─────────────────────────

    public ObservableCollection<LiveReadItem> LiveReads { get; } = [];

    /// <summary>Direção fixa do modo manual; null = parado ou DOCAS (ver <see cref="IsDocksActive"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeBannerText), nameof(ModeBannerHint), nameof(IsEntradaActive),
        nameof(IsSaidaActive), nameof(IsStopped), nameof(IsModeActive))]
    private OperationMode? _activeMode;

    /// <summary>Modo DOCAS (POR ANTENA): entrada e saída simultâneas, direção pela antena que leu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeBannerText), nameof(ModeBannerHint), nameof(IsStopped), nameof(IsModeActive))]
    private bool _isDocksActive;

    /// <summary>Docas do leitor conectado; null = leitor sem docas configuradas (só modo manual).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocksKeyHint), nameof(DocksAvailable))]
    [NotifyCanExecuteChangedFor(nameof(DocasCommand))]
    private AntennaDirectionMap? _dockMap;

    public bool DocksAvailable => DockMap is not null;

    public string DocksKeyHint => DockMap is { } map ? map.Describe() : "CONFIGURAR ANTENAS";

    public string ModeBannerText => (ActiveMode, IsDocksActive) switch
    {
        (OperationMode.Entrada, _) => "ENTRADA",
        (OperationMode.Saida, _) => "SAÍDA",
        (_, true) => "DOCAS",
        _ => "PARADO",
    };

    public string ModeBannerHint => (ActiveMode, IsDocksActive) switch
    {
        (OperationMode.Entrada, _) => "Tag lida → DENTRO do estoque",
        (OperationMode.Saida, _) => "Tag lida → FORA do estoque",
        (_, true) => DockMap is { } map ? DescribeDocks(map) : "Direção pela antena que leu",
        _ => "Leituras não alteram o estoque",
    };

    public bool IsEntradaActive => ActiveMode == OperationMode.Entrada;
    public bool IsSaidaActive => ActiveMode == OperationMode.Saida;
    public bool IsStopped => ActiveMode is null && !IsDocksActive;
    public bool IsModeActive => !IsStopped;

    private static string DescribeDocks(AntennaDirectionMap map)
    {
        var parts = new List<string>(2);
        if (map.Entrada.Count > 0)
            parts.Add($"ANT {string.Join(",", map.Entrada)} → DENTRO");
        if (map.Saida.Count > 0)
            parts.Add($"ANT {string.Join(",", map.Saida)} → FORA");
        return string.Join(" · ", parts);
    }

    private void SetActiveOperation(InventoryOperation? operation)
    {
        ActiveMode = operation?.FixedMode;
        IsDocksActive = operation?.IsByAntenna == true;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EntradaCommand), nameof(SaidaCommand), nameof(DocasCommand), nameof(PararCommand), nameof(ApplyReaderCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EntradaCommand), nameof(SaidaCommand), nameof(DocasCommand), nameof(PararCommand))]
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

    private bool CanOperateDocks() => CanOperate() && DockMap is not null;

    /// <summary>Entrada e saída ao mesmo tempo: a antena que leu define a direção (leitor multiporta).</summary>
    [RelayCommand(CanExecute = nameof(CanOperateDocks))]
    private Task DocasAsync() =>
        DockMap is { } map ? RunModeCommandAsync(p => p.SetAntennaModeAsync(map), "iniciar DOCAS") : Task.CompletedTask;

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
            SetActiveOperation(handle.Pipeline.CurrentOperation);
            StatusMessage = IsStopped
                ? "Inventário parado."
                : IsDocksActive ? $"DOCAS ativo: {DockMap?.Describe()}." : $"{Display.Mode(ActiveMode)} ativo.";
        }
        catch (Exception ex)
        {
            ReportError($"Falha ao {description}: {ex.Message}");
            if (_current is { } h)
                SetActiveOperation(h.Pipeline.CurrentOperation);
        }
        finally
        {
            IsBusy = false;
            _lifecycleLock.Release();
        }
    }

    // ───────────────────────── Abas ─────────────────────────

    public const int MovimentacoesTabIndex = 2;
    public const int CadastroTabIndex = 3;
    public const int EmbalagensTabIndex = 4;

    [ObservableProperty] private int _selectedTabIndex;

    // Recarrega ao abrir: as listas refletem leituras feitas na aba Operação.
    partial void OnSelectedTabIndexChanged(int value)
    {
        if (!_repositoryReady)
            return;
        if (value == MovimentacoesTabIndex)
            _ = Movimentacoes.LoadAsync();
        else if (value == CadastroTabIndex)
            _ = Cadastro.LoadAsync();
        else if (value == EmbalagensTabIndex)
            _ = Embalagens.LoadAsync();
    }

    public StockMovementsViewModel Movimentacoes { get; }

    public DatabaseSettingsViewModel Banco { get; }

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

    /// <summary>
    /// Abre a tag selecionada no cadastro: etiqueta de embalagem abre a embalagem na aba Embalagens; as demais, a aba
    /// Cadastro (novo cadastro ou edição, se já cadastrada).
    /// </summary>
    [RelayCommand]
    private async Task RegisterSelectedTagAsync()
    {
        if (SelectedTag is not { } tag)
            return;
        if (tag.IsPackageTag)
        {
            SelectedTabIndex = EmbalagensTabIndex;
            if (await Embalagens.OpenByEpcAsync(tag.Epc))
                return;
        }
        SelectedTabIndex = CadastroTabIndex;
        await Cadastro.OpenEpcAsync(tag.Epc);
    }

    // ───────────────────────── Embalagens ─────────────────────────

    public PackageCatalogViewModel Embalagens { get; }

    /// <summary>
    /// Recarrega o mapa EPC → embalagem usado pela passagem (veredito por item). Chamado ao abrir o banco e após
    /// gravar/remover embalagem. A movimentação no banco não depende dele: o lote resolve a embalagem na transação.
    /// </summary>
    private async Task RefreshPackageDirectoryAsync()
    {
        var map = await Task.Run(() => _packages.GetPackageEpcMapAsync());
        _packageDirectory.Replace(map);
        Interlocked.Exchange(ref _countersDirty, 1);
    }

    // ───────────────────────── Cadastro ─────────────────────────

    public TagCatalogViewModel Cadastro { get; }

    /// <summary>
    /// Captura para o cadastro. Usa o mesmo lock de troca de modo/leitor: com a captura rodando, ENTRADA/SAÍDA
    /// ficam bloqueados, e vice-versa. Retorna null (com o motivo na aba que pediu) quando não pode capturar.
    /// </summary>
    private async Task<TagCaptureResult?> CaptureForCatalogAsync(Action<string> reportUnavailable)
    {
        if (IsModeActive)
        {
            reportUnavailable(
                $"Modo {ModeBannerText} ativo: a leitura seria gravada no estoque. Pressione PARAR na aba Operação antes de ler para cadastro.");
            return null;
        }

        if (!await _lifecycleLock.WaitAsync(0))
        {
            reportUnavailable("Leitor ocupado (troca de modo ou de leitor em andamento). Tente de novo.");
            return null;
        }

        IsBusy = true;
        try
        {
            var handle = _current;
            if (handle is null || !IsPipelineRunning || _shutdownStarted)
            {
                reportUnavailable("Leitor não conectado. Verifique a aba Configuração.");
                return null;
            }

            StatusMessage = "Lendo tag para cadastro...";
            var result = await Task.Run(() => handle.Pipeline.CaptureAsync(CaptureWindow));
            StatusMessage = $"Leitura para cadastro: {result.Tags.Count} tag(s) no campo.";
            return result;
        }
        finally
        {
            IsBusy = false;
            _lifecycleLock.Release();
        }
    }

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

    /// <summary>Na ordem de <see cref="ReaderType"/>.</summary>
    public string[] ReaderTypes { get; } = ["Serial", "TCP/IP", "LLRP · rede", "Impinj IoT · REST"];
    public IReadOnlyList<string> Protocols { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerialSelected), nameof(IsTcpSelected), nameof(IsNetworkSelected),
        nameof(IsProtocolSelectable), nameof(IsImpinjIotSelected))]
    private int _readerTypeIndex;

    private ReaderType SelectedReaderType => (ReaderType)Math.Clamp(ReaderTypeIndex, 0, ReaderTypes.Length - 1);

    public bool IsSerialSelected => SelectedReaderType == ReaderType.Serial;
    public bool IsTcpSelected => SelectedReaderType == ReaderType.Tcp;

    /// <summary>Host/porta valem para TCP, LLRP e Impinj IoT.</summary>
    public bool IsNetworkSelected => SelectedReaderType != ReaderType.Serial;

    /// <summary>Protocolo plugável só existe nos transportes genéricos (Serial/TCP).</summary>
    public bool IsProtocolSelectable => SelectedReaderType is ReaderType.Serial or ReaderType.Tcp;

    public bool IsImpinjIotSelected => SelectedReaderType == ReaderType.ImpinjIot;

    [ObservableProperty] private string? _selectedProtocol;
    [ObservableProperty] private string? _serialPortName;
    [ObservableProperty] private decimal? _baudRate;
    [ObservableProperty] private string? _tcpHost;
    [ObservableProperty] private decimal? _tcpPort;
    [ObservableProperty] private decimal? _powerDbm;
    [ObservableProperty] private string? _readerAntennasText;
    [ObservableProperty] private string? _antennaPowerText;
    [ObservableProperty] private bool _enforceAnatelBand;
    [ObservableProperty] private string? _dockEntradaText;
    [ObservableProperty] private string? _dockSaidaText;
    [ObservableProperty] private bool _iotUseHttps;
    [ObservableProperty] private string? _iotUsername;
    [ObservableProperty] private string? _iotPassword;
    [ObservableProperty] private string? _configMessage;

    // ── Modelo do leitor (catálogo) ──

    public IReadOnlyList<ReaderModel> ReaderModels { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelPowerMin), nameof(ModelPowerMax), nameof(ModelPowerHint),
        nameof(ModelValidationText), nameof(IsModelValidated), nameof(ModelSpecs), nameof(IsCustomModel),
        nameof(BandMinimum), nameof(BandMaximum))]
    private ReaderModel _selectedReaderModel;

    public bool IsCustomModel => SelectedReaderModel.IsCustom;
    public bool IsModelValidated => SelectedReaderModel.Validated;
    public string ModelValidationText => SelectedReaderModel.Validated ? "VALIDADO EM HARDWARE" : "NÃO VALIDADO EM HARDWARE";

    public decimal ModelPowerMin => (decimal)(SelectedReaderModel.IsCustom ? 0 : SelectedReaderModel.MinPowerDbm);
    public decimal ModelPowerMax => (decimal)(SelectedReaderModel.IsCustom ? 33 : SelectedReaderModel.MaxPowerDbm);
    public string ModelPowerHint => $"{ModelPowerMin:0}–{ModelPowerMax:0} dBm neste modelo · controla o alcance";

    /// <summary>Ficha técnica do modelo selecionado (rótulo, valor); itens vazios são omitidos.</summary>
    public IReadOnlyList<SpecItem> ModelSpecs
    {
        get
        {
            var m = SelectedReaderModel;
            var connection = m.Type switch
            {
                ReaderType.Tcp => $"TCP/IP · porta {m.DefaultTcpPort}",
                ReaderType.Llrp => $"LLRP · TCP {m.DefaultTcpPort}",
                ReaderType.ImpinjIot => $"HTTPS REST · porta {m.DefaultTcpPort}",
                _ => $"Serial · {m.DefaultBaudRate} baud 8N1",
            };
            SpecItem[] items =
            [
                new("Família", m.Family),
                new("Chip / firmware", m.Chip),
                new("Interface", m.Interface),
                new("Conexão padrão", connection),
                new("Protocolo", m.Protocol),
                new("Frequência", m.FrequencyBand),
                new("Quadro", m.FrameFormat),
                new("Potência", m.IsCustom ? "" : $"{m.MinPowerDbm:0}–{m.MaxPowerDbm:0} dBm (padrão {m.DefaultPowerDbm:0})"),
                new("Antenas", m.IsCustom ? "" : m.AntennaPorts.ToString(CultureInfo.InvariantCulture)),
                new("Observações", m.Notes),
            ];
            return items.Where(i => !string.IsNullOrWhiteSpace(i.Value)).ToArray();
        }
    }

    /// <summary>Selecionar um modelo preenche tipo, protocolo, conexão, potência e antenas com os padrões dele.</summary>
    partial void OnSelectedReaderModelChanged(ReaderModel value)
    {
        if (value is null || value.IsCustom)
            return;

        ReaderTypeIndex = (int)value.Type;
        if (Protocols.Contains(value.Protocol, StringComparer.OrdinalIgnoreCase))
            SelectedProtocol = Protocols.First(p => string.Equals(p, value.Protocol, StringComparison.OrdinalIgnoreCase));
        if (value.Type == ReaderType.Serial)
            BaudRate = value.DefaultBaudRate;
        if (value.Type != ReaderType.Serial)
            TcpPort = value.DefaultTcpPort;
        if (value.Type == ReaderType.ImpinjIot)
            IotUseHttps = value.DefaultTcpPort != 80;
        PowerDbm = (decimal)value.DefaultPowerDbm;
        ReaderAntennasText = "1";
        if (value.AntennaPorts < 2)
            DockEntradaText = DockSaidaText = "";
        ConfigMessage = $"Padrões de \"{value.DisplayName}\" carregados. Clique em \"Aplicar leitor\" para conectar.";
    }

    // ── Portas seriais detectadas ──

    [ObservableProperty] private IReadOnlyList<string> _availableSerialPorts = [];
    [ObservableProperty] private string? _selectedAvailablePort;

    partial void OnSelectedAvailablePortChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            SerialPortName = value;
    }

    [RelayCommand]
    private void RefreshSerialPorts()
    {
        try
        {
            AvailableSerialPorts = System.IO.Ports.SerialPort.GetPortNames()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            AvailableSerialPorts = [];
            ConfigMessage = $"Não foi possível listar as portas seriais: {ex.Message}";
        }
    }

    public string SettingsFilePath => ReaderSettingsStore.FilePath;

    /// <summary>Grava o leitor configurado na tela em appsettings.Local.json (vale a partir da próxima abertura).</summary>
    [RelayCommand]
    private async Task SaveReaderAsDefaultAsync()
    {
        if (!TryBuildReaderOptions(out var options, out var error))
        {
            ConfigMessage = error;
            return;
        }

        try
        {
            await Task.Run(() => _saveReaderOptions(options));
            ConfigMessage = $"Salvo como padrão em {SettingsFilePath}. Vale na próxima abertura do app.";
        }
        catch (Exception ex)
        {
            ConfigMessage = $"Falha ao salvar: {ex.Message}";
        }
    }

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
        if (IsModeActive || _current?.Pipeline.CurrentOperation is not null)
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
            _current?.Monitor.SetMinRssi(MinRssi);
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
        var type = SelectedReaderType;

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

        if (!TryParseAntennaPower(AntennaPowerText, out var antennaPower, out error))
            return false;
        if (antennaPower.Keys.FirstOrDefault(port => !antennas.Contains(port)) is > 0 and var inactive)
        {
            error = $"Potência definida para a porta {inactive}, que não está nas antenas ativas.";
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
        else
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

        if (IsProtocolSelectable && string.IsNullOrWhiteSpace(SelectedProtocol))
        {
            error = "Selecione um protocolo.";
            return false;
        }

        var baseIot = _baseReaderOptions.ImpinjIot;
        var iot = new ImpinjIotReaderOptions
        {
            UseHttps = IotUseHttps,
            Username = IotUsername?.Trim() ?? "",
            Password = IotPassword ?? "",
            AllowSelfSignedCertificate = baseIot.AllowSelfSignedCertificate,
            PresetId = baseIot.PresetId,
        };
        if (type == ReaderType.ImpinjIot && iot.Username.Length == 0)
        {
            error = "Informe o usuário da API REST do leitor.";
            return false;
        }

        if (!TryBuildInventoryOptions(out var inventory, out error))
            return false;

        if (!TryBuildDockOptions(antennas, out var docks, out error))
            return false;

        options = new ReaderOptions
        {
            Model = SelectedReaderModel.Id,
            Type = type,
            Protocol = type switch
            {
                ReaderType.Llrp => "LLRP",
                ReaderType.ImpinjIot => "ImpinjIoT",
                _ => SelectedProtocol ?? _baseReaderOptions.Protocol,
            },
            Serial = serial,
            Tcp = tcp,
            PowerDbm = (double)power,
            Antennas = antennas,
            AntennaPowerDbm = antennaPower,
            EnforceAnatelBand = EnforceAnatelBand,
            Llrp = _baseReaderOptions.Llrp,
            ImpinjIot = iot,
            ReconnectDelayMs = _baseReaderOptions.ReconnectDelayMs,
            Inventory = inventory,
            Docks = docks,
        };
        error = "";
        return true;
    }

    /// <summary>
    /// Docas por antena: vazio nas duas = só modo manual. Exige leitor multiporta (perfil de 1 porta é recusado:
    /// com uma antena só não há como separar as docas).
    /// </summary>
    private bool TryBuildDockOptions(int[] activeAntennas, out DockOptions docks, out string error)
    {
        var current = _baseReaderOptions.Docks ?? new DockOptions();
        docks = new DockOptions { ArbitrationMs = current.ArbitrationMs, ZoneHoldMs = current.ZoneHoldMs };
        if (!TryParseAntennas(DockEntradaText, allowEmpty: true, out var entrada)
            || !TryParseAntennas(DockSaidaText, allowEmpty: true, out var saida))
        {
            error = "Antenas das docas inválidas: use números separados por vírgula (ex.: 1,2).";
            return false;
        }

        docks.EntradaAntennas = entrada;
        docks.SaidaAntennas = saida;
        if (!docks.IsConfigured)
        {
            error = "";
            return true;
        }

        if (!SelectedReaderModel.IsCustom && SelectedReaderModel.AntennaPorts < 2)
        {
            error = $"\"{SelectedReaderModel.DisplayName}\" tem 1 porta de antena: docas exigem leitor multiporta. Deixe as docas vazias e use ENTRADA/SAÍDA manual.";
            return false;
        }

        return AntennaDirectionMap.TryCreate(docks, activeAntennas, out _, out error);
    }

    /// <summary>Formato "porta:dBm" separado por vírgula/ponto e vírgula (ex.: "1:30; 2:27,5"). Vazio = mesma potência em todas.</summary>
    internal static bool TryParseAntennaPower(string? text, out Dictionary<int, double> power, out string error)
    {
        power = [];
        error = "";
        // Vírgula antes de "porta:" separa pares ("1:30,2:27"); as demais são decimais ("2:27,5").
        var normalized = PowerPairSeparator().Replace(text ?? "", ";");
        foreach (var item in normalized.Split(PowerSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(PowerKeySeparators, 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 64
                || !double.TryParse(parts[1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var dbm)
                || dbm is < 0 or > 33)
            {
                error = $"Potência por porta inválida em \"{item}\": use porta:dBm (0–33), ex.: 1:30; 2:27.";
                return false;
            }

            power[port] = dbm;
        }

        return true;
    }

    private static readonly char[] PowerSeparators = [';'];
    private static readonly char[] PowerKeySeparators = [':', '='];

    [System.Text.RegularExpressions.GeneratedRegex(@",(?=\s*\d+\s*[:=])")]
    private static partial System.Text.RegularExpressions.Regex PowerPairSeparator();

    internal static string FormatAntennaPower(IReadOnlyDictionary<int, double>? power) =>
        power is null || power.Count == 0
            ? ""
            : string.Join("; ", power.OrderBy(p => p.Key).Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Key}:{p.Value:0.##}")));

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

            _ = Cadastro.LoadAsync();
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
            try
            {
                await RefreshPackageDirectoryAsync();
            }
            catch (Exception ex)
            {
                ReportError($"Embalagens não carregadas (a passagem conta cada etiqueta como item): {ex.Message}");
            }
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
        DockMap = null;
        if (options.Docks is { IsConfigured: true } docks)
        {
            if (AntennaDirectionMap.TryCreate(docks, options.Antennas, out var map, out var dockError))
                DockMap = map;
            else
                ReportError($"Docas desativadas: {dockError}");
        }

        StatusMessage = $"Conectando a {ReaderName}...";
        try
        {
            await Task.Run(() => handle.Pipeline.StartAsync());
            handle.Monitor.SetMinRssi(MinRssi);
            handle.Monitor.Start();
            IsPipelineRunning = true;
            SetActiveOperation(handle.Pipeline.CurrentOperation);
            StatusMessage = DockMap is null
                ? "Leitor pronto. Selecione ENTRADA ou SAÍDA."
                : "Leitor pronto. Selecione DOCAS (entrada e saída pela antena) ou ENTRADA/SAÍDA manual.";
            if (handle.Reader is IReaderInfoSource { Info: { } info })
                ConfigMessage = $"Leitor identificado: {info.Description}";
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
        try { await Task.Run(async () => await handle.Monitor.DisposeAsync()); }
        catch (Exception ex) { ReportError($"Erro ao liberar o monitor de passagem: {ex.Message}"); }
        try { await Task.Run(async () => await handle.Pipeline.DisposeAsync()); }
        catch (Exception ex) { ReportError($"Erro ao liberar o pipeline: {ex.Message}"); }
        try { await Task.Run(async () => await handle.Reader.DisposeAsync()); }
        catch (Exception ex) { ReportError($"Erro ao liberar o leitor: {ex.Message}"); }

        _current = null;
        ResetSignal();
        ResetPassage();
        SetActiveOperation(null);
        DockMap = null;
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
        DocasCommand.NotifyCanExecuteChanged();
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
        handle.Pipeline.OperationChanged += OnOperationChanged;
        handle.Pipeline.Error += OnPipelineError;
        handle.Reader.TagRead += OnReaderTagRead;
        AttachPassage(handle.Monitor);
    }

    private void Detach(PipelineHandle handle)
    {
        handle.Pipeline.BatchPersisted -= OnBatchPersisted;
        handle.Pipeline.OperationChanged -= OnOperationChanged;
        handle.Pipeline.Error -= OnPipelineError;
        handle.Reader.TagRead -= OnReaderTagRead;
        DetachPassage(handle.Monitor);
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

    private void OnOperationChanged(object? sender, InventoryOperation? operation) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(sender, _current?.Pipeline))
                SetActiveOperation(operation);
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
                    $"descartadas {s.StaleDiscarded:N0} · persistidas {s.Persisted:N0} · drops de fila {s.QueueDropped:N0}" +
                    (IsDocksActive ? $" · cruzadas {s.CrossReads:N0} · sem doca {s.UnmappedAntenna:N0}" : "");

                UpdateInventoryControl(handle.Monitor);
                var connected = handle.Reader.IsConnected;
                UpdateSignal(connected && handle.Reader.IsInventoryRunning);
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
