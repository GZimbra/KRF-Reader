using KrfReader.Application.Pipeline;
using KrfReader.Application.Readers;

namespace KrfReader.UI;

/// <summary>
/// Leitor + pipeline + monitor de passagem montados juntos: a UI precisa do nome/estado do leitor para a barra de
/// status, e o pipeline não o expõe. A UI descarta monitor, pipeline e leitor, nessa ordem.
/// </summary>
public sealed record PipelineHandle(IInventoryPipeline Pipeline, IRfidReader Reader, PassageMonitor Monitor);
