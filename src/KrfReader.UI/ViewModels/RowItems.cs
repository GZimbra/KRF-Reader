using System.Globalization;
using KrfReader.Application.Persistence;
using KrfReader.Domain;

namespace KrfReader.UI.ViewModels;

internal static class Display
{
    public static string Mode(OperationMode? mode) => mode switch
    {
        OperationMode.Entrada => "ENTRADA",
        OperationMode.Saida => "SAÍDA",
        _ => "PARADO",
    };

    public static string Status(TagStatus status) => status == TagStatus.Dentro ? "DENTRO" : "FORA";

    public static string LocalTime(DateTime utc, string format = "HH:mm:ss") =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString(format, CultureInfo.CurrentCulture);

    public static string Rssi(double rssi) => rssi.ToString("0.0", CultureInfo.CurrentCulture);
}

/// <summary>Uma leitura aplicada no banco (lista ao vivo da aba Operação). Imutável: criada fora da UI thread.</summary>
public sealed class LiveReadItem
{
    public LiveReadItem(AppliedRead applied)
    {
        Time = Display.LocalTime(applied.Read.TimestampUtc, "HH:mm:ss.fff");
        Epc = applied.Read.Epc;
        Antenna = applied.Read.AntennaId;
        Rssi = Display.Rssi(applied.Read.Rssi);
        RssiValue = applied.Read.Rssi;
        Mode = Display.Mode(applied.Mode);
        IsAnomaly = applied.IsAnomaly;
        Result = applied.Event switch
        {
            EventType.Criacao => "CRIADA",
            EventType.MudancaStatus => applied.NewStatus == TagStatus.Dentro ? "→ DENTRO" : "→ FORA",
            EventType.SaidaSemEntrada => "⚠ SAÍDA SEM ENTRADA",
            _ => "sem mudança",
        };
    }

    public string Time { get; }
    public string Epc { get; }
    public int Antenna { get; }
    public string Rssi { get; }
    public double RssiValue { get; }
    public string Mode { get; }
    public string Result { get; }
    public bool IsAnomaly { get; }
}

public sealed class TagRowItem(TagState state)
{
    public string Epc { get; } = state.Epc;
    public string Descricao { get; } = state.Descricao ?? "não cadastrada";
    public bool IsRegistered { get; } = state.IsRegistered;
    public string Status { get; } = Display.Status(state.Status);
    public bool IsDentro { get; } = state.Status == TagStatus.Dentro;
    public string UltimaLeitura { get; } = Display.LocalTime(state.UltimaLeituraUtc, "dd/MM/yyyy HH:mm:ss");
    public string UltimaMudanca { get; } = Display.LocalTime(state.UltimaAtualizacaoStatusUtc, "dd/MM/yyyy HH:mm:ss");
}

public sealed class EventRowItem(InventoryEvent ev)
{
    public string Time { get; } = Display.LocalTime(ev.TimestampUtc, "dd/MM/yyyy HH:mm:ss");
    public string Mode { get; } = Display.Mode(ev.Mode);
    public string Type { get; } = ev.Type switch
    {
        EventType.Criacao => "Criação (→ DENTRO)",
        EventType.MudancaStatus => ev.Mode == OperationMode.Entrada ? "Mudança → DENTRO" : "Mudança → FORA",
        EventType.SaidaSemEntrada => "⚠ SAÍDA SEM ENTRADA",
        _ => ev.Type.ToString(),
    };
    public int Antenna { get; } = ev.AntennaId;
    public string Rssi { get; } = Display.Rssi(ev.Rssi);
    public bool IsAnomaly { get; } = ev.IsAnomaly;
}

/// <summary>Linha da aba Movimentações.</summary>
public sealed class MovementRowItem(StockMovement m)
{
    public string Time { get; } = Display.LocalTime(m.DataHoraUtc, "dd/MM/yyyy HH:mm:ss");
    public string Tipo { get; } = Display.Mode(m.Tipo);
    public bool IsEntrada { get; } = m.Tipo == OperationMode.Entrada;
    public string Epc { get; } = m.Epc;
    public string Item { get; } = m.Descricao is null ? "não cadastrada" : m.Sku is null ? m.Descricao : $"{m.Sku} · {m.Descricao}";
    public bool IsRegistered { get; } = m.Descricao is not null;
    public string Evento { get; } = m.Evento switch
    {
        EventType.Criacao => "1ª entrada",
        EventType.MudancaStatus => m.Tipo == OperationMode.Entrada ? "→ DENTRO" : "→ FORA",
        EventType.SaidaSemEntrada => "⚠ SAÍDA SEM ENTRADA",
        _ => m.Evento.ToString(),
    };
    public int Antenna { get; } = m.AntennaId;
    public string Rssi { get; } = Display.Rssi(m.Rssi);
    public string Passagem { get; } = m.PassagemId is { } id ? $"#{id}" : "—";
    public bool IsAnomaly { get; } = m.IsAnomaly;
}

/// <summary>Linha da ficha técnica do modelo de leitor (Configurações).</summary>
public sealed record SpecItem(string Label, string Value);
