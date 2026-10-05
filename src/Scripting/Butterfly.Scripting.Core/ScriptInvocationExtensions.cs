using System.Globalization;

namespace Butterfly.Scripting
{
    public static class ScriptInvocationExtensions
    {
        public static Task<object?> InvokeScriptAsync(this IScriptInvoker invoker, ScriptingLanguage language, string scriptContent, object? parameters, IServiceProvider? services = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(invoker, nameof(invoker));
            return invoker.InvokeScriptAsync(language, scriptContent, ScriptParameters.From(parameters), services, cancellationToken);
        }
        public static async Task<T?> InvokeScriptAsync<T>(this IScriptInvoker invoker, ScriptingLanguage language, string scriptContent, object? parameters = null, IServiceProvider? services = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(invoker, nameof(invoker));
            var result = await invoker.InvokeScriptAsync(language, scriptContent, ScriptParameters.From(parameters), services, cancellationToken).ConfigureAwait(false);
            return ConvertResult<T>(language, result);
        }

        static T? ConvertResult<T>(ScriptingLanguage language, object? result)
        {
            if (result is null)
                return default;
            if (result is T typed)
                return typed;

            var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            try
            {
                if (result is IConvertible && typeof(IConvertible).IsAssignableFrom(targetType))
                    return (T)Convert.ChangeType(result, targetType, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new ScriptException(language, $"The script returned {result.GetType()}, which cannot be converted to {typeof(T)}.", ex);
            }
            throw new ScriptException(language, $"The script returned {result.GetType()}, which cannot be converted to {typeof(T)}.");
        }
    }
}
