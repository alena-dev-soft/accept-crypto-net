// Scripted payer for the local chain: `pay <invoice>`. Implementation comes in a later step.
if (args.Length != 1)
{
    await Console.Error.WriteLineAsync("Usage: pay <invoice-token-or-url>").ConfigureAwait(false);
    return 2;
}

await Console.Error.WriteLineAsync($"Paying invoice '{args[0]}' is not implemented yet.").ConfigureAwait(false);
return 1;
