using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace Butterfly.Scripting.Lua.MoonSharp
{
    // MoonSharp asks the attached debugger whether to pause before every instruction, in every coroutine and inside CLR callbacks
    // such as a table.sort comparator. Throwing there stops the script, and pcall cannot catch it because it is not a Lua error.
    internal sealed class CancellationDebugger(CancellationToken cancellationToken) : IDebugger
    {
        static readonly DebuggerAction Run = new() { Action = DebuggerAction.ActionType.Run };

        public bool IsPauseRequested()
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        public DebuggerAction GetAction(int ip, SourceRef sourceref) => Run;
        public DebuggerCaps GetDebuggerCaps() => default;
        public List<DynamicExpression> GetWatchItems() => [];
        public bool SignalRuntimeException(ScriptRuntimeException ex) => false;

        public void SetDebugService(DebugService debugService) { }
        public void SetSourceCode(SourceCode sourceCode) { }
        public void SetByteCode(string[] byteCode) { }
        public void SignalExecutionEnded() { }
        public void Update(WatchType watchType, IEnumerable<WatchItem> items) { }
        public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
    }
}
