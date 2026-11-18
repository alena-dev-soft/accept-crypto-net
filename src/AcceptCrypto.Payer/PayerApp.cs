using System.Globalization;
using AcceptCrypto.Domain.Money;
using AcceptCrypto.LocalChain;
using Nethereum.Util;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;

namespace AcceptCrypto.Payer;

/// <summary>Executes the <c>pay</c> commands. Output goes to <paramref name="output"/>.</summary>
internal sealed class PayerApp(TextWriter output, Func<string, string?> environment)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>USDC minted to the payer by <c>pay setup</c>.</summary>
    public static readonly UsdcAmount SetupBalance = UsdcAmount.Parse("1000000");

    public async Task<int> RunAsync(CommandLine cmd, CancellationToken ct)
    {
        switch (cmd.Command)
        {
            case "help":
                await output.WriteLineAsync(CommandLine.Usage).ConfigureAwait(false);
                return 0;
            case "setup":
                return await SetupAsync(cmd, ct).ConfigureAwait(false);
            case "balance":
                return await BalanceAsync(cmd).ConfigureAwait(false);
            case "mine":
                return await MineAsync(cmd).ConfigureAwait(false);
            case "reorg":
                return await ReorgAsync(cmd).ConfigureAwait(false);
            case "warp":
                return await WarpAsync(cmd).ConfigureAwait(false);
            case "xpub":
                return await XpubAsync(cmd).ConfigureAwait(false);
            default:
                return await PayAsync(cmd, ct).ConfigureAwait(false);
        }
    }

    private string RpcUrl(CommandLine cmd) =>
        cmd.Option("--rpc") ?? environment("ACCEPTCRYPTO_RPC_URL") ?? AnvilAccounts.DefaultRpcUrl;

    private string TokenAddress(CommandLine cmd, string? fromInvoice = null)
    {
        var address = cmd.Option("--token") ?? fromInvoice ?? environment("ACCEPTCRYPTO_TOKEN_ADDRESS") ?? AnvilAccounts.MockUsdcAddress;
        return ValidAddress(address, "token");
    }

    private async Task<int> SetupAsync(CommandLine cmd, CancellationToken ct)
    {
        var rpc = RpcUrl(cmd);
        await RequireLocalChainAsync(rpc, "setup").ConfigureAwait(false);
        var deployer = new Web3(new Account(AnvilAccounts.Deployer.PrivateKey, AnvilAccounts.ChainId), rpc);
        var (token, deployed) = await MockUsdc.EnsureDeployedAsync(
            deployer, TokenAddress(cmd), AnvilAccounts.Payer.Address, SetupBalance, ct).ConfigureAwait(false);
        var balance = await token.BalanceOfAsync(AnvilAccounts.Payer.Address).ConfigureAwait(false);
        await output.WriteLineAsync(deployed
            ? $"Mock USDC deployed at {token.Address} (6 decimals)."
            : $"Mock USDC already deployed at {token.Address}.").ConfigureAwait(false);
        await output.WriteLineAsync($"Payer {AnvilAccounts.Payer.Address} holds {balance.ToFixedString()} USDC.").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> BalanceAsync(CommandLine cmd)
    {
        var account = cmd.Arguments.Count > 0 ? ValidAddress(cmd.Arguments[0], "account") : PayerAccount(cmd).Address;
        var token = new MockUsdc(new Web3(RpcUrl(cmd)), TokenAddress(cmd));
        var balance = await token.BalanceOfAsync(account).ConfigureAwait(false);
        await output.WriteLineAsync($"{account}: {balance.ToFixedString()} USDC").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> MineAsync(CommandLine cmd)
    {
        var blocks = cmd.Arguments.Count > 0 ? PositiveInt(cmd.Arguments[0], "blocks") : 1;
        var anvil = new AnvilClient(RpcUrl(cmd));
        await anvil.MineAsync(blocks).ConfigureAwait(false);
        await output.WriteLineAsync($"Mined {blocks} block(s); head is #{await anvil.GetBlockNumberAsync().ConfigureAwait(false)}.").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ReorgAsync(CommandLine cmd)
    {
        if (cmd.Arguments.Count != 1)
        {
            throw new FormatException("Usage: pay reorg <depth>");
        }

        var depth = PositiveInt(cmd.Arguments[0], "depth");
        var rpc = RpcUrl(cmd);
        await RequireLocalChainAsync(rpc, "reorg").ConfigureAwait(false);
        var head = await ReorgChainAsync(rpc, depth).ConfigureAwait(false);
        await output.WriteLineAsync($"Reorged the last {depth} block(s); their transactions are gone. Head #{head!.Number} {head.Hash}.").ConfigureAwait(false);
        return 0;
    }

    /// <summary>Drops the last <paramref name="depth"/> blocks (anvil_reorg) and re-syncs the chain clock.</summary>
    private static async Task<BlockInfo?> ReorgChainAsync(string rpc, int depth)
    {
        var anvil = new AnvilClient(rpc);
        await anvil.ReorgAsync(depth).ConfigureAwait(false);
        var head = await anvil.GetBlockAsync().ConfigureAwait(false);

        // anvil_reorg re-mines the replaced blocks with older timestamps, so the chain clock falls behind the wall
        // clock by ~depth × block time. Payment deadlines are compared with block timestamps (on time vs paid_late),
        // so re-sync the clock: otherwise a later "pay after expiry" demo would still count as on time.
        var now = DateTimeOffset.UtcNow;
        if (head is not null && head.Timestamp < now.AddSeconds(-1))
        {
            await anvil.SetNextBlockTimestampAsync(now.AddSeconds(1)).ConfigureAwait(false);
        }

        return head;
    }

    private async Task<int> WarpAsync(CommandLine cmd)
    {
        if (cmd.Arguments.Count != 1)
        {
            throw new FormatException("Usage: pay warp <seconds>");
        }

        var seconds = PositiveInt(cmd.Arguments[0], "seconds");
        var rpc = RpcUrl(cmd);
        await RequireLocalChainAsync(rpc, "warp").ConfigureAwait(false);
        var head = await new AnvilClient(rpc).WarpAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        await output.WriteLineAsync($"Chain time moved by {seconds}s: block #{head.Number} at {head.Timestamp:u}.").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> PayAsync(CommandLine cmd, CancellationToken ct)
    {
        var to = cmd.Option("--to");
        var amountOption = cmd.Option("--amount");
        UsdcAmount? amount = amountOption is null ? null : ParseAmount(amountOption);
        var parts = cmd.Option("--split") is { } s ? PositiveInt(s, "--split") : 1;

        var thenReorg = cmd.Flag("--then-reorg");
        if (thenReorg && to is not null)
        {
            throw new FormatException("--then-reorg needs an invoice (it watches the invoice status), not --to.");
        }

        string recipient;
        string? invoiceToken = null;
        InvoiceReference? reference = null;
        PaymentDetailsClient? detailsClient = null;
        string? apiKey = null;
        long? expectedChainId = null;
        if (to is not null)
        {
            if (cmd.Arguments.Count > 0)
            {
                throw new FormatException("Give either an invoice or --to <address>, not both.");
            }

            recipient = ValidAddress(to, "--to");
            if (amount is null)
            {
                throw new FormatException("--to needs --amount.");
            }
        }
        else
        {
            if (cmd.Arguments.Count != 1)
            {
                throw new FormatException("Usage: pay <invoice> [--amount X] [--split N]");
            }

            var explicitService = cmd.Option("--service") ?? environment("ACCEPTCRYPTO_SERVICE_URL");
            var serviceUrl = new Uri(explicitService ?? "http://localhost:5052");
            reference = InvoiceReference.Parse(cmd.Arguments[0], serviceUrl, explicitService is null ? null : serviceUrl);
            apiKey = cmd.Option("--api-key") ?? environment("ACCEPTCRYPTO_API_KEY");
            detailsClient = new PaymentDetailsClient(Http);
            var details = await detailsClient.GetAsync(reference, apiKey, ct).ConfigureAwait(false);

            await output.WriteLineAsync(
                $"Invoice: pay {details.Amount.ToFixedString()} USDC to {details.Address}" +
                (details.Status is null ? string.Empty : $" (status {details.Status})") +
                (details.ExpiresAt is { } exp ? $", deadline {exp:u}" : string.Empty)).ConfigureAwait(false);

            recipient = ValidAddress(details.Address, "invoice address");
            invoiceToken = details.TokenAddress;
            expectedChainId = details.ChainId;
            amount ??= details.Amount;
        }

        var rpc = RpcUrl(cmd);
        var account = PayerAccount(cmd);
        var anvil = new AnvilClient(rpc);
        var chainId = await anvil.GetChainIdAsync().ConfigureAwait(false);
        if (expectedChainId is { } expected && expected != chainId)
        {
            throw new InvalidOperationException($"The invoice is on chain {expected}, but {rpc} is chain {chainId}.");
        }

        if (thenReorg && chainId != AnvilAccounts.ChainId)
        {
            throw new InvalidOperationException("--then-reorg only works on a local Anvil chain.");
        }

        if (account.IsDefault && chainId != AnvilAccounts.ChainId)
        {
            throw new InvalidOperationException(
                $"Chain {chainId} is not a local Anvil chain; the well-known Anvil key is only used locally. Pass --private-key.");
        }

        var web3 = new Web3(new Account(account.PrivateKey, chainId), rpc);
        var token = new MockUsdc(web3, TokenAddress(cmd, invoiceToken));
        var transfers = PaymentPlan.Split(amount.Value, parts);
        var balance = await token.BalanceOfAsync(account.Address).ConfigureAwait(false);
        if (balance < amount.Value)
        {
            throw new InvalidOperationException(
                $"Payer {account.Address} holds {balance.ToFixedString()} USDC, needs {amount.Value.ToFixedString()}." +
                (account.IsDefault ? " Run 'pay setup' first." : string.Empty));
        }

        long? firstBlock = null;
        for (var i = 0; i < transfers.Count; i++)
        {
            var receipt = await token.TransferAsync(recipient, transfers[i], ct).ConfigureAwait(false);
            firstBlock ??= (long)receipt.BlockNumber.Value;
            await output.WriteLineAsync(
                $"[{i + 1}/{transfers.Count}] sent {transfers[i].ToFixedString()} USDC to {recipient}: tx {receipt.TransactionHash} in block #{receipt.BlockNumber.Value}")
                .ConfigureAwait(false);
        }

        var after = await token.BalanceOfAsync(account.Address).ConfigureAwait(false);
        await output.WriteLineAsync($"Done. Payer {account.Address} now holds {after.ToFixedString()} USDC.").ConfigureAwait(false);
        return thenReorg ? await ReorgPaymentAsync(rpc, firstBlock!.Value, detailsClient!, reference!, apiKey, ct).ConfigureAwait(false) : 0;
    }

    /// <summary>
    /// <c>--then-reorg</c> (FR-10 demo): waits until the service has seen the payment (<c>confirming</c>), then drops
    /// every block from the payment's first block on — in one process, so it is in time even when starting the payer
    /// twice would be too slow for the confirmation count.
    /// </summary>
    private async Task<int> ReorgPaymentAsync(
        string rpc, long firstBlock, PaymentDetailsClient client, InvoiceReference reference, string? apiKey, CancellationToken ct)
    {
        await output.WriteLineAsync("Waiting for the service to report the payment as confirming …").ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string? status = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            status = (await client.GetAsync(reference, apiKey, ct).ConfigureAwait(false)).Status;
            if (status is not ("pending" or "underpaid" or "expired" or null))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
        }

        if (status != "confirming")
        {
            await output.WriteLineAsync($"Not reorging: the invoice is '{status}', not 'confirming' (is the service running?).").ConfigureAwait(false);
            return 1;
        }

        var anvil = new AnvilClient(rpc);
        var depth = checked((int)(await anvil.GetBlockNumberAsync().ConfigureAwait(false) - firstBlock + 1));
        var head = await ReorgChainAsync(rpc, depth).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Reorged the last {depth} block(s) (from #{firstBlock}): the payment is gone from the chain. Head #{head!.Number} {head.Hash}.")
            .ConfigureAwait(false);
        return 0;
    }

    private (string Address, string PrivateKey, bool IsDefault) PayerAccount(CommandLine cmd)
    {
        var key = cmd.Option("--private-key") ?? environment("PAYER_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            return (AnvilAccounts.Payer.Address, AnvilAccounts.Payer.PrivateKey, true);
        }

        var account = new Account(key.Trim());
        return (account.Address, key.Trim(), string.Equals(account.Address, AnvilAccounts.Payer.Address, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task RequireLocalChainAsync(string rpc, string what)
    {
        var chainId = await new AnvilClient(rpc).GetChainIdAsync().ConfigureAwait(false);
        if (chainId != AnvilAccounts.ChainId)
        {
            throw new InvalidOperationException($"'{what}' only runs against a local Anvil chain (31337); {rpc} is chain {chainId}.");
        }
    }

    internal static UsdcAmount ParseAmount(string text)
    {
        if (!UsdcAmount.TryParse(text, out var amount) || amount.IsZero)
        {
            throw new FormatException($"'{text}' is not a positive USDC amount (decimal, at most 6 decimals, e.g. 12.5).");
        }

        return amount;
    }

    private static int PositiveInt(string text, string name) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new FormatException($"{name} must be a positive integer, got '{text}'.");

    /// <summary><c>pay xpub [new]</c>: the merchant xpub for a TESTNET deployment (docs/testnet.md). Offline.</summary>
    private async Task<int> XpubAsync(CommandLine cmd)
    {
        string mnemonic;
        if (cmd.Arguments is ["new"])
        {
            mnemonic = MerchantXpub.NewMnemonic();
            await output.WriteLineAsync($"""
                NEW TEST MNEMONIC (shown once; store it offline, e.g. in a password manager, never in git):

                  {mnemonic}

                TESTNET ONLY. Never send mainnet funds to addresses of this mnemonic.

                """).ConfigureAwait(false);
        }
        else if (cmd.Arguments.Count == 0)
        {
            mnemonic = environment("MERCHANT_MNEMONIC")
                ?? throw new FormatException("Set MERCHANT_MNEMONIC (e.g. read -rs MERCHANT_MNEMONIC; export MERCHANT_MNEMONIC) or run 'pay xpub new'.");
        }
        else
        {
            throw new FormatException("Usage: pay xpub [new]");
        }

        var (xpub, addresses) = MerchantXpub.Derive(mnemonic);
        await output.WriteLineAsync($"Merchant xpub ({MerchantXpub.AccountPath}) — put it into Merchant__Xpub:").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync($"  {xpub}").ConfigureAwait(false);
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("First deposit addresses (invoice index i = MetaMask \"Account i+1\" of this mnemonic):").ConfigureAwait(false);
        for (var i = 0; i < addresses.Count; i++)
        {
            await output.WriteLineAsync($"  #{i}  {addresses[i]}").ConfigureAwait(false);
        }

        return 0;
    }

    private static string ValidAddress(string address, string name) =>
        AddressUtil.Current.IsValidEthereumAddressHexFormat(address)
            ? AddressUtil.Current.ConvertToChecksumAddress(address)
            : throw new FormatException($"{name} '{address}' is not a 0x-prefixed 20-byte address.");
}
