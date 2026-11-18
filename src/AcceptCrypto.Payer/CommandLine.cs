namespace AcceptCrypto.Payer;

/// <summary>Parsed command line of <c>pay</c>.</summary>
internal sealed class CommandLine
{
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--amount", "--split", "--to", "--rpc", "--token", "--private-key", "--service", "--api-key",
    };

    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "setup", "balance", "mine", "reorg", "warp", "xpub", "help",
    };

    /// <summary>Command: <c>pay</c> (default), <c>setup</c>, <c>balance</c>, <c>mine</c>, <c>reorg</c>, <c>warp</c>, <c>xpub</c>, <c>help</c>.</summary>
    public string Command { get; private init; } = "pay";

    /// <summary>Positional arguments after the command (for <c>pay</c>: the invoice reference).</summary>
    public IReadOnlyList<string> Arguments { get; private init; } = [];

    /// <summary>Options with values (<c>--name value</c> or <c>--name=value</c>).</summary>
    public IReadOnlyDictionary<string, string> Options { get; private init; } = new Dictionary<string, string>();

    public string? Option(string name) => Options.TryGetValue(name, out var value) ? value : null;

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                return new CommandLine { Command = "help" };
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var eq = arg.IndexOf('=', StringComparison.Ordinal);
                var name = eq > 0 ? arg[..eq] : arg;
                if (!ValueOptions.Contains(name))
                {
                    throw new FormatException($"Unknown option '{name}'.");
                }

                string value;
                if (eq > 0)
                {
                    value = arg[(eq + 1)..];
                }
                else
                {
                    if (i + 1 >= args.Count)
                    {
                        throw new FormatException($"Option '{name}' needs a value.");
                    }

                    value = args[++i];
                }

                if (!options.TryAdd(name, value))
                {
                    throw new FormatException($"Option '{name}' is given twice.");
                }

                continue;
            }

            positional.Add(arg);
        }

        var command = "pay";
        if (positional.Count > 0 && Commands.Contains(positional[0]))
        {
            command = positional[0];
            positional.RemoveAt(0);
        }

        if (positional.Count == 0 && options.Count == 0 && command == "pay")
        {
            command = "help";
        }

        return new CommandLine { Command = command, Arguments = positional, Options = options };
    }

    public const string Usage = """
        AcceptCrypto.NET scripted payer — TEST ONLY, local Anvil chain (or a testnet with your own --private-key).

        Usage:
          pay <invoice> [--amount X] [--split N]   pay an invoice (exact amount by default)
          pay --to <address> --amount X [--split N] transfer to any address (e.g. an unmatched payment, S7)
          pay setup                                 deploy mock USDC (if missing) and mint 1,000,000 USDC to the payer
          pay balance [address]                     USDC balance (default: the payer)
          pay mine [blocks]                         mine blocks (default 1), e.g. to add confirmations
          pay reorg <depth>                         forced reorg: drop the transactions of the last <depth> blocks
          pay warp <seconds>                        move chain time forward and mine a block (late payments, S4)
          pay xpub [new]                            merchant xpub (m/44'/60'/0'/0) for a TESTNET wallet: from the
                                                    mnemonic in MERCHANT_MNEMONIC, or 'new' = generate a fresh one

        <invoice>: payment page URL (http://host/pay/<token>), public token, or invoice ID (GUID, needs --api-key).
          --amount X    USDC amount instead of the invoice amount (less = underpay, more = overpay)
          --split N     send the amount as N transfers that add up exactly (partial payments)

        Options (environment variable in brackets):
          --rpc URL           JSON-RPC URL            [ACCEPTCRYPTO_RPC_URL]       default http://localhost:8545
          --token ADDRESS     USDC contract           [ACCEPTCRYPTO_TOKEN_ADDRESS] default mock USDC on Anvil
          --service URL       AcceptCrypto service    [ACCEPTCRYPTO_SERVICE_URL]   default http://localhost:5052;
                              when set, it also wins over the host of a payment page URL (Docker)
          --api-key KEY       merchant API key        [ACCEPTCRYPTO_API_KEY]       only for invoice IDs
          --private-key KEY   payer key               [PAYER_PRIVATE_KEY]          default Anvil account #1 (local only)
        """;
}
