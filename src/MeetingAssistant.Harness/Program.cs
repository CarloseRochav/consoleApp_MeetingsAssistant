// Harness de consola: autotests y utilidades de la app. Cada comando vive en
// CommandCatalog; aca solo se separa --prompt del resto y se despacha.
//
//   (sin argumentos) | <segundos>   graba y corre el pipeline de verdad (cobra)
//   --<comando> [argumentos]        ver --help

string? promptId = null;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--prompt", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        promptId = args[++i];
        continue;
    }

    positional.Add(args[i]);
}

if (positional.Count == 0 || !positional[0].StartsWith("--", StringComparison.Ordinal))
{
    return await PipelineRunCommand.RecordAsync(new CommandContext(positional, promptId));
}

if (positional[0] is "--help" or "-h")
{
    PrintHelp();
    return 0;
}

HarnessCommand? command = CommandCatalog.Find(positional[0]);
if (command is null)
{
    Console.Error.WriteLine($"Comando desconocido: {positional[0]}");
    PrintHelp();
    return 1;
}

IReadOnlyList<string> arguments = positional.Skip(1).ToList();
if (!command.AcceptsArgumentCount(arguments.Count))
{
    Console.Error.WriteLine($"Uso: {command.Usage}");
    return 1;
}

return await command.RunAsync(new CommandContext(arguments, promptId));

static void PrintHelp()
{
    Console.WriteLine("Uso: MeetingAssistant.Harness [<segundos> | --<comando> [argumentos]] [--prompt <id>]");
    Console.WriteLine("Sin comando, graba esos segundos (15 por defecto) y corre el pipeline de verdad (cobra).");
    Console.WriteLine();
    foreach (HarnessCommand command in CommandCatalog.All)
    {
        Console.WriteLine($"  {command.Usage,-50} {command.Description}");
    }
}
