using KrfReader.Domain;

namespace KrfReader.Application.Pipeline;

/// <summary>Leitura carimbada, no momento do recebimento, com o modo e a geração vigentes.</summary>
internal readonly record struct StampedRead(TagRead Read, OperationMode Mode, long Generation);

internal enum PipelineMessageKind : byte
{
    Read,

    /// <summary>Barreira de flush do PARAR: replicada para todos os workers e contada na persistência.</summary>
    Barrier,

    /// <summary>Aviso à persistência de que a geração mudou (descartar o buffer sem esperar o próximo flush).</summary>
    GenerationChanged,
}

/// <summary>Mensagem das filas internas: leitura, barreira ou aviso de troca de geração.</summary>
internal readonly record struct PipelineMessage(PipelineMessageKind Kind, StampedRead Read, FlushBarrier? Barrier)
{
    public static PipelineMessage ForRead(in StampedRead read) => new(PipelineMessageKind.Read, read, null);

    public static PipelineMessage ForBarrier(FlushBarrier barrier) => new(PipelineMessageKind.Barrier, default, barrier);

    public static PipelineMessage GenerationChanged => new(PipelineMessageKind.GenerationChanged, default, null);
}

/// <summary>
/// Barreira atingida quando a persistência recebe o marcador de todos os workers: como cada fila é FIFO,
/// nesse ponto todas as leituras anteriores à barreira já chegaram à persistência.
/// </summary>
internal sealed class FlushBarrier(int participants)
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _remaining = participants;

    public Task Completion => _completion.Task;

    /// <summary>Registra a chegada de um worker. Retorna true para o último.</summary>
    public bool Arrive() => Interlocked.Decrement(ref _remaining) == 0;

    public void Complete() => _completion.TrySetResult();
}
