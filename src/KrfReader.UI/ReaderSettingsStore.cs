using System.Text.Json;
using System.Text.Json.Nodes;
using KrfReader.Application.Configuration;

namespace KrfReader.UI;

/// <summary>
/// Grava as seções <c>Reader</c> e <c>Database</c> em <c>appsettings.Local.json</c> (ao lado do executável), que
/// sobrescreve o appsettings.json na próxima abertura. As demais seções do arquivo local são preservadas.
/// </summary>
public static class ReaderSettingsStore
{
    public const string FileName = "appsettings.Local.json";

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static Task SaveAsync(ReaderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var inventory = options.Inventory ?? new InventoryOptions();
        var docks = options.Docks ?? new DockOptions();
        return SaveSectionAsync(ReaderOptions.SectionName, new JsonObject
        {
            [nameof(ReaderOptions.Model)] = options.Model,
            [nameof(ReaderOptions.Type)] = options.Type.ToString(),
            [nameof(ReaderOptions.Protocol)] = options.Protocol,
            [nameof(ReaderOptions.Serial)] = new JsonObject
            {
                [nameof(SerialReaderOptions.PortName)] = options.Serial.PortName,
                [nameof(SerialReaderOptions.BaudRate)] = options.Serial.BaudRate,
                [nameof(SerialReaderOptions.DataBits)] = options.Serial.DataBits,
                [nameof(SerialReaderOptions.Parity)] = options.Serial.Parity,
                [nameof(SerialReaderOptions.StopBits)] = options.Serial.StopBits,
            },
            [nameof(ReaderOptions.Tcp)] = new JsonObject
            {
                [nameof(TcpReaderOptions.Host)] = options.Tcp.Host,
                [nameof(TcpReaderOptions.Port)] = options.Tcp.Port,
                [nameof(TcpReaderOptions.ConnectTimeoutMs)] = options.Tcp.ConnectTimeoutMs,
            },
            [nameof(ReaderOptions.PowerDbm)] = options.PowerDbm,
            [nameof(ReaderOptions.Antennas)] = new JsonArray(options.Antennas.Select(a => (JsonNode)a).ToArray()),
            [nameof(ReaderOptions.AntennaPowerDbm)] = new JsonObject((options.AntennaPowerDbm ?? []).OrderBy(p => p.Key)
                .Select(p => KeyValuePair.Create(p.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), (JsonNode?)p.Value))),
            [nameof(ReaderOptions.EnforceAnatelBand)] = options.EnforceAnatelBand,
            [nameof(ReaderOptions.Llrp)] = new JsonObject
            {
                [nameof(LlrpReaderOptions.KeepaliveMs)] = options.Llrp.KeepaliveMs,
                [nameof(LlrpReaderOptions.ResponseTimeoutMs)] = options.Llrp.ResponseTimeoutMs,
                [nameof(LlrpReaderOptions.ResetToFactoryDefault)] = options.Llrp.ResetToFactoryDefault,
            },
            [nameof(ReaderOptions.ImpinjIot)] = new JsonObject
            {
                [nameof(ImpinjIotReaderOptions.UseHttps)] = options.ImpinjIot.UseHttps,
                [nameof(ImpinjIotReaderOptions.Username)] = options.ImpinjIot.Username,
                [nameof(ImpinjIotReaderOptions.Password)] = options.ImpinjIot.Password,
                [nameof(ImpinjIotReaderOptions.AllowSelfSignedCertificate)] = options.ImpinjIot.AllowSelfSignedCertificate,
                [nameof(ImpinjIotReaderOptions.PresetId)] = options.ImpinjIot.PresetId,
            },
            [nameof(ReaderOptions.ReconnectDelayMs)] = options.ReconnectDelayMs,
            [nameof(ReaderOptions.Docks)] = new JsonObject
            {
                [nameof(DockOptions.EntradaAntennas)] = new JsonArray((docks.EntradaAntennas ?? []).Select(a => (JsonNode)a).ToArray()),
                [nameof(DockOptions.SaidaAntennas)] = new JsonArray((docks.SaidaAntennas ?? []).Select(a => (JsonNode)a).ToArray()),
                [nameof(DockOptions.ArbitrationMs)] = docks.ArbitrationMs,
                [nameof(DockOptions.ZoneHoldMs)] = docks.ZoneHoldMs,
            },
            [nameof(ReaderOptions.Inventory)] = new JsonObject
            {
                [nameof(InventoryOptions.Session)] = inventory.Session.ToString(),
                [nameof(InventoryOptions.Target)] = inventory.Target.ToString(),
                [nameof(InventoryOptions.TargetDwellMs)] = inventory.TargetDwellMs,
                [nameof(InventoryOptions.InitialQ)] = inventory.InitialQ,
                [nameof(InventoryOptions.AdaptiveQ)] = inventory.AdaptiveQ,
                [nameof(InventoryOptions.MinQ)] = inventory.MinQ,
                [nameof(InventoryOptions.MaxQ)] = inventory.MaxQ,
                [nameof(InventoryOptions.SelectEpcPrefix)] = inventory.SelectEpcPrefix,
                [nameof(InventoryOptions.CommandGapMs)] = inventory.CommandGapMs,
            },
        }, cancellationToken);
    }

    /// <summary>Provider + connection string. Vale na próxima abertura (o repositório é montado na inicialização).</summary>
    public static Task SaveAsync(DatabaseOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SaveSectionAsync(DatabaseOptions.SectionName, new JsonObject
        {
            [nameof(DatabaseOptions.Provider)] = options.Provider,
            [nameof(DatabaseOptions.ConnectionString)] = options.ConnectionString,
        }, cancellationToken);
    }

    private static async Task SaveSectionAsync(string sectionName, JsonObject section, CancellationToken cancellationToken)
    {
        var path = FilePath;

        JsonObject root;
        if (File.Exists(path))
        {
            await using var input = File.OpenRead(path);
            root = await JsonNode.ParseAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonObject ?? [];
        }
        else
        {
            root = [];
        }

        root[sectionName] = section;

        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
            .ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }
}
