using KrfReader.Application.Configuration;
using KrfReader.Application.Persistence;
using KrfReader.Application.Pipeline;
using KrfReader.Application.Readers;
using KrfReader.Infrastructure.Persistence;
using KrfReader.Infrastructure.Readers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KrfReader.UI;

/// <summary>
/// Único ponto que conhece as implementações concretas. O resto da UI depende só das interfaces.
/// </summary>
public sealed class CompositionRoot : IDisposable
{
    private readonly IRfidReaderFactory _readerFactory;

    private CompositionRoot(
        IConfiguration configuration,
        ReaderOptions readerOptions,
        PipelineOptions pipelineOptions,
        PassageOptions passageOptions,
        DatabaseOptions databaseOptions)
    {
        PassageOptions = passageOptions;
        Configuration = configuration;
        ReaderOptions = readerOptions;
        PipelineOptions = pipelineOptions;
        DatabaseOptions = databaseOptions;

        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss.fff ";
            });
        });

        if (!string.Equals(databaseOptions.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Database:Provider '{databaseOptions.Provider}' não suportado (apenas Sqlite).");

        var sqlite = new SqliteInventoryRepository(databaseOptions, LoggerFactory.CreateLogger<SqliteInventoryRepository>());
        Repository = sqlite;
        Catalog = sqlite;
        ProtocolFactory = new ReaderProtocolFactory();
        _readerFactory = new RfidReaderFactory(ProtocolFactory, LoggerFactory);
    }

    public IConfiguration Configuration { get; }
    public ReaderOptions ReaderOptions { get; }
    public PipelineOptions PipelineOptions { get; }
    public PassageOptions PassageOptions { get; }
    public DatabaseOptions DatabaseOptions { get; }
    public ILoggerFactory LoggerFactory { get; }
    public IInventoryRepository Repository { get; }
    public ITagCatalogRepository Catalog { get; }
    public IReaderProtocolFactory ProtocolFactory { get; }
    public IReaderModelCatalog ReaderModels { get; } = ReaderModelCatalog.Default;

    /// <summary>
    /// Carrega appsettings.json (obrigatório) e appsettings.Local.json (opcional, sobrescreve) de
    /// <see cref="AppContext.BaseDirectory"/>. Não faz I/O de banco: <see cref="IInventoryRepository.InitializeAsync"/>
    /// é chamado depois, de forma assíncrona, pela UI.
    /// </summary>
    public static CompositionRoot Create()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .Build();

        // appsettings.Local.json de versões anteriores pode ter Reader:Type = "Mock" (simulador removido):
        // nesse caso o leitor volta ao padrão (R200 serial) em vez de falhar no Bind.
        var typeKey = $"{ReaderOptions.SectionName}:{nameof(ReaderOptions.Type)}";
        if (configuration[typeKey] is { } typeText && !Enum.TryParse<ReaderType>(typeText, ignoreCase: true, out _))
        {
            var defaults = new ReaderOptions();
            configuration = new ConfigurationBuilder()
                .AddConfiguration(configuration)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [typeKey] = defaults.Type.ToString(),
                    [$"{ReaderOptions.SectionName}:{nameof(ReaderOptions.Model)}"] = defaults.Model,
                    [$"{ReaderOptions.SectionName}:{nameof(ReaderOptions.Protocol)}"] = defaults.Protocol,
                })
                .Build();
        }

        var reader = new ReaderOptions();
        configuration.GetSection(ReaderOptions.SectionName).Bind(reader);
        var pipeline = new PipelineOptions();
        configuration.GetSection(PipelineOptions.SectionName).Bind(pipeline);
        var passage = new PassageOptions();
        configuration.GetSection(PassageOptions.SectionName).Bind(passage);
        var database = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(database);

        // O binder ANEXA itens de array ao valor padrão (ex.: Antennas = [1] + [1,2] = [1,1,2]):
        // quando a chave existe no JSON, ela substitui o padrão.
        reader.Antennas = ReadArray(configuration, $"{ReaderOptions.SectionName}:{nameof(ReaderOptions.Antennas)}") ?? reader.Antennas;
        pipeline.AllowedAntennas = ReadArray(configuration, $"{PipelineOptions.SectionName}:{nameof(PipelineOptions.AllowedAntennas)}") ?? pipeline.AllowedAntennas;

        return new CompositionRoot(configuration, reader, pipeline, passage, database);
    }

    private static int[]? ReadArray(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        if (!section.Exists() && section.Value is null)
            return null;
        return section.Get<int[]>() ?? [];
    }

    /// <summary>
    /// Cria leitor + pipeline + monitor de passagem (ainda não iniciados). Chamado de novo a cada troca de leitor em runtime.
    /// </summary>
    public PipelineHandle CreatePipeline(ReaderOptions readerOptions)
    {
        var reader = _readerFactory.Create(readerOptions);
        var pipeline = new InventoryPipeline(
            reader,
            Repository,
            PipelineOptions,
            LoggerFactory.CreateLogger<InventoryPipeline>());
        var monitor = new PassageMonitor(
            pipeline,
            reader,
            readerOptions.Inventory ?? new InventoryOptions(),
            PassageOptions,
            PipelineOptions.MinRssi,
            LoggerFactory.CreateLogger<PassageMonitor>());
        return new PipelineHandle(pipeline, reader, monitor);
    }

    public void Dispose() => LoggerFactory.Dispose();
}
