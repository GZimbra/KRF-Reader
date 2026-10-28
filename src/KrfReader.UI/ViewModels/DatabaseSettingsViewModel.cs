using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KrfReader.Application.Configuration;
using KrfReader.Infrastructure.Persistence;

namespace KrfReader.UI.ViewModels;

/// <summary>
/// Card "Banco de dados" da aba Configurações: escolhe SQLite ou SQL Server e a connection string.
/// Não troca o banco em uso: grava em appsettings.Local.json e vale na próxima abertura (repositório, pipeline e
/// abas são montados sobre o banco na inicialização; trocar a quente arriscaria lote gravado no banco errado).
/// </summary>
public sealed partial class DatabaseSettingsViewModel : ObservableObject
{
    private readonly DatabaseOptions _active;
    private readonly Func<DatabaseOptions, Task> _save;
    private readonly Func<DatabaseOptions, CancellationToken, Task<string>> _test;

    public DatabaseSettingsViewModel(
        DatabaseOptions active,
        Func<DatabaseOptions, Task> save,
        Func<DatabaseOptions, CancellationToken, Task<string>> test)
    {
        _active = active;
        _save = save;
        _test = test;

        var provider = DatabaseProviders.Normalize(active.Provider) ?? DatabaseProviders.Sqlite;
        _providerIndex = IndexOf(provider);
        _connectionString = active.ConnectionString;
        ActiveText = $"EM USO · {ProviderLabels[IndexOf(provider)]}";
    }

    /// <summary>Mesma ordem de <see cref="DatabaseProviders.All"/>.</summary>
    public string[] ProviderLabels { get; } = ["SQLite · arquivo local", "SQL Server · Express/Standard"];

    public string ActiveText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSqlServer), nameof(PowerBiText))]
    private int _providerIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerBiText))]
    private string _connectionString;

    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _messageIsError;
    [ObservableProperty] private bool _isTesting;

    public bool IsSqlServer => SelectedProvider == DatabaseProviders.SqlServer;

    private string SelectedProvider => DatabaseProviders.All[Math.Clamp(ProviderIndex, 0, DatabaseProviders.All.Count - 1)];

    /// <summary>O que digitar no Power BI. Só faz sentido para SQL Server (SQLite não tem DirectQuery).</summary>
    public string PowerBiText => DatabaseConnectionTester.PowerBiTarget(Current()) is { } target
        ? $"Power BI → Obter dados → SQL Server · Servidor: {target.Server} · Banco: {target.Database} · Modo: DirectQuery · tabelas: TagsCadastradas, Movimentacoes, Passagens"
        : IsSqlServer
            ? "Connection string sem Server/Database válidos."
            : "SQLite não suporta DirectQuery: no Power BI só importação com atualização manual/agendada. Use SQL Server para tempo real.";

    /// <summary>Trocar o provider troca a connection string padrão, se o usuário ainda não personalizou.</summary>
    partial void OnProviderIndexChanged(int value)
    {
        var provider = SelectedProvider;
        if (ConnectionString is DatabaseProviders.DefaultSqliteConnectionString or DatabaseProviders.DefaultSqlServerConnectionString
            || string.IsNullOrWhiteSpace(ConnectionString))
        {
            ConnectionString = provider == DatabaseProviders.SqlServer
                ? DatabaseProviders.DefaultSqlServerConnectionString
                : DatabaseProviders.DefaultSqliteConnectionString;
        }
        Message = "";
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        IsTesting = true;
        Message = "Testando...";
        MessageIsError = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var options = Current();
            Message = await Task.Run(() => _test(options, timeout.Token));
        }
        catch (Exception ex)
        {
            Message = $"FALHA · {ex.Message}";
            MessageIsError = true;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var options = Current();
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            Message = "Connection string vazia.";
            MessageIsError = true;
            return;
        }

        try
        {
            await Task.Run(() => _save(options));
            var unchanged = string.Equals(options.Provider, DatabaseProviders.Normalize(_active.Provider), StringComparison.Ordinal)
                && string.Equals(options.ConnectionString, _active.ConnectionString, StringComparison.Ordinal);
            Message = unchanged
                ? $"Salvo em {ReaderSettingsStore.FilePath}. Já é o banco em uso."
                : $"Salvo em {ReaderSettingsStore.FilePath}. FECHE E ABRA O APP para usar o novo banco. Dados do banco anterior não são copiados.";
            MessageIsError = false;
        }
        catch (Exception ex)
        {
            Message = $"Falha ao salvar: {ex.Message}";
            MessageIsError = true;
        }
    }

    private DatabaseOptions Current() => new()
    {
        Provider = SelectedProvider,
        ConnectionString = ConnectionString?.Trim() ?? "",
    };

    private static int IndexOf(string provider) =>
        Math.Max(0, DatabaseProviders.All.ToList().IndexOf(provider));
}
