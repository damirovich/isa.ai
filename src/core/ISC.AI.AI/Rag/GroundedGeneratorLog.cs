using Microsoft.Extensions.Logging;

namespace ISC.AI.AI.Rag;

/// <summary>Логи RAG-оркестратора через source-generated <c>LoggerMessage</c> (CA1848).</summary>
internal static partial class GroundedGeneratorLog
{
    // «Не тихое» усечение: если контекст обрезан по бюджету токенов, это фиксируется, а не проглатывается.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RAG: контекст усечён по бюджету токенов — в промпт вошло {Included} из {Retrieved} фрагментов (бюджет {Budget} ток.)")]
    public static partial void ContextTruncated(ILogger logger, int included, int retrieved, int budget);
}
