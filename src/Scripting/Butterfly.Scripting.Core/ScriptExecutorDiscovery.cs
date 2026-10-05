using System.Reflection;

namespace Butterfly.Scripting
{
    internal static class ScriptExecutorDiscovery
    {
        internal static IEnumerable<IScriptExecutor> CreateExecutors(IEnumerable<Assembly> assemblies)
            => assemblies
                .Where(assembly => !assembly.IsDynamic)
                .SelectMany(GetLoadableTypes)
                .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IScriptExecutor)) && type.GetConstructor(Type.EmptyTypes) is not null)
                .Select(type => (IScriptExecutor)Activator.CreateInstance(type)!);

        static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.OfType<Type>();
            }
        }
    }
}
