using Butterfly.Tool.Cli;
using Butterfly.Tool.Commands;

// To add a command: implement ICommand in Commands/ and register it here.
ICommand[] commands =
[
    new AddCommand(),
    new RemoveCommand(),
    new ListCommand(),
    new InitCommand(),
    new VersionCommand(),
];

return new CommandApp("butterfly", commands).Run(args);
