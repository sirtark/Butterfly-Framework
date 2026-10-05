namespace Butterfly.Tool.Cli
{
    public sealed class ParsedArguments
    {
        private readonly Dictionary<string, string?> _options = [];

        private ParsedArguments(List<string> positionals) => Positionals = positionals;

        public IReadOnlyList<string> Positionals { get; }

        public bool Has(CommandOption option) => _options.ContainsKey(option.Name);

        public string? Get(CommandOption option) => _options.GetValueOrDefault(option.Name);

        public static ParsedArguments Parse(IReadOnlyList<string> tokens, IReadOnlyList<CommandOption> options)
        {
            List<string> positionals = [];
            ParsedArguments parsed = new(positionals);

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];

                if (!token.StartsWith('-') || token == "-")
                {
                    positionals.Add(token);
                    continue;
                }

                CommandOption option = options.FirstOrDefault(o => o.Matches(token))
                    ?? throw new CliException($"Unknown option '{token}'.");

                if (!option.HasValue)
                {
                    parsed._options[option.Name] = null;
                    continue;
                }

                if (i + 1 >= tokens.Count)
                    throw new CliException($"Option '{option.Name}' requires a value <{option.ValueName}>.");

                parsed._options[option.Name] = tokens[++i];
            }

            return parsed;
        }
    }
}
