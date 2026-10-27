using System.Text.Json;
using System.Text.Json.Nodes;
using KrfReader.Application.Configuration;

namespace KrfReader.UI;

/// <summary>
/// Grava a seção <c>Reader</c> em <c>appsettings.Local.json</c> (ao lado do executável), que sobrescreve o
/// appsettings.json na próxima abertura. As demais seções do arquivo local são preservadas.
/// </summary>
public static class ReaderSettingsStore
{
    public const string FileName = "appsettings.Local.json";

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static async Task SaveAsync(ReaderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
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

        root[ReaderOptions.SectionName] = new JsonObject
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
            [nameof(ReaderOptions.ReconnectDelayMs)] = options.ReconnectDelayMs,
        };

        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
            .ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }
}
