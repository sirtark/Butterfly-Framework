namespace Butterfly.Tool.Cli
{
    public static class Output
    {
        public static void Info(string message) => Console.WriteLine(message);

        public static void Warning(string message) => Write(Console.Error, ConsoleColor.Yellow, "warning: " + message);

        public static void Error(string message) => Write(Console.Error, ConsoleColor.Red, "error: " + message);

        private static void Write(TextWriter writer, ConsoleColor color, string message)
        {
            bool colored = !Console.IsErrorRedirected;
            if (colored)
                Console.ForegroundColor = color;

            writer.WriteLine(message);

            if (colored)
                Console.ResetColor();
        }
    }
}
