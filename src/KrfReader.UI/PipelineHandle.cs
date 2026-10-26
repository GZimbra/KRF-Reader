using KrfReader.Application.Pipeline;
using KrfReader.Application.Readers;

namespace KrfReader.UI;

/// <summary>
/// Leitor + pipeline montados juntos: a UI precisa do nome/estado do leitor para a barra de status,
/// e o pipeline não o expõe. A UI descarta os dois (pipeline primeiro, depois o leitor).
/// </summary>
public sealed record PipelineHandle(IInventoryPipeline Pipeline, IRfidReader Reader);
