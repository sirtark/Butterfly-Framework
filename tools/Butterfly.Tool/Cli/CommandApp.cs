namespace Butterfly.Tool.Cli
{
    public sealed class CommandApp(string name, IReadOnlyList<ICommand> commands)
    {
        public static readonly CommandOption HelpOption = new("--help", "-h", "Show help for the command.");

        public int Run(string[] args)
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            if (args[0] is "--version")
                args[0] = "version";

            ICommand? command = commands.FirstOrDefault(c => c.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));
            if (command is null)
            {
                Output.Error($"Unknown command '{args[0]}'. Run '{name} --help' to see the available commands.");
                return 1;
            }

            try
            {
                ParsedArguments arguments = ParsedArguments.Parse(args[1..], [.. command.Options, HelpOption]);
                if (arguments.Has(HelpOption))
                {
                    PrintHelp(command);
                    return 0;
                }

                return command.Execute(arguments);
            }
            catch (CliException ex)
            {
                Output.Error(ex.Message);
                return 1;
            }
        }

        private void PrintHelp()
        {
            Console.WriteLine($"Butterfly CLI {ButterflyFramework.Version}");
            Console.WriteLine();
            Console.WriteLine($"Usage: {name} <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Commands:");
            int width = commands.Max(c => c.Name.Length) + 2;
            foreach (ICommand command in commands)
                Console.WriteLine($"  {command.Name.PadRight(width)}{command.Description}");
            Console.WriteLine();
            Console.WriteLine($"Run '{name} <command> --help' for more information on a command.");
        }

        private void PrintHelp(ICommand command)
        {
            Console.WriteLine(command.Description);
            Console.WriteLine();
            Console.WriteLine($"Usage: {name} {command.Name} {command.Arguments} [options]".Replace("  ", " "));
            Console.WriteLine();
            Console.WriteLine("Options:");

            IEnumerable<CommandOption> options = [.. command.Options, HelpOption];
            string Signature(CommandOption o) =>
                (o.Alias is null ? "" : o.Alias + ", ") + o.Name + (o.HasValue ? $" <{o.ValueName}>" : "");

            int width = options.Max(o => Signature(o).Length) + 2;
            foreach (CommandOption option in options)
                Console.WriteLine($"  {Signature(option).PadRight(width)}{option.Description}");
        }
    }
}
