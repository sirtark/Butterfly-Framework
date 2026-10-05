namespace Butterfly.Scripting.CSharp
{
    [Flags]
    public enum CSharpScriptFlags : uint
    {
        None,
        DependencyInjection = 1 << 0,
    }
}
