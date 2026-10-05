namespace Butterfly.Scripting
{
    public sealed class ScriptValidationResult
    {
        ScriptValidationResult(IReadOnlyList<string> errors) => Errors = errors;

        public static ScriptValidationResult Success { get; } = new([]);

        public IReadOnlyList<string> Errors { get; }
        public bool IsValid => Errors.Count == 0;

        public static ScriptValidationResult Failure(params IEnumerable<string> errors)
        {
            string[] list = [.. errors];
            if (list.Length == 0)
                throw new ArgumentException("A failed validation requires at least one error.", nameof(errors));
            return new(list);
        }
    }
}
