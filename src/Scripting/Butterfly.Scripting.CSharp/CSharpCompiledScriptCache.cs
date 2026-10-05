using Microsoft.CodeAnalysis.Scripting;
using System.Diagnostics.CodeAnalysis;

namespace Butterfly.Scripting.CSharp
{
    internal sealed class CSharpCompiledScriptCache(int capacity)
    {
        readonly Dictionary<string, LinkedListNode<(string Source, Script Script)>> _entries = [];
        readonly LinkedList<(string Source, Script Script)> _usage = new();
        readonly Lock _lock = new();

        public bool TryGet(string source, [NotNullWhen(true)] out Script? script)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(source, out var node))
                {
                    _usage.Remove(node);
                    _usage.AddFirst(node);
                    script = node.Value.Script;
                    return true;
                }
            }
            script = null;
            return false;
        }

        public void Add(string source, Script script)
        {
            lock (_lock)
            {
                if (_entries.Remove(source, out var existing))
                    _usage.Remove(existing);
                _entries[source] = _usage.AddFirst((source, script));
                if (_entries.Count > capacity)
                {
                    _entries.Remove(_usage.Last!.Value.Source);
                    _usage.RemoveLast();
                }
            }
        }
    }
}
