using System.Text.Json;

namespace Butterfly.Scripting.AspNetCore
{
    public sealed class ScriptInvocationRequest
    {
        public required string Source { get; init; }
        public Dictionary<string, JsonElement>? Parameters { get; init; }
    }
}
