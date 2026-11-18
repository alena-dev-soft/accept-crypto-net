using Nethereum.JsonRpc.Client;

namespace AcceptCrypto.Payer;

/// <summary>Scripted payer for the local chain (TEST ONLY): <c>pay &lt;invoice&gt; [--amount X] [--split N]</c> and chain helpers.</summary>
internal static class PayerProgram
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var commandLine = CommandLine.Parse(args);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            return await new PayerApp(Console.Out, Environment.GetEnvironmentVariable).RunAsync(commandLine, cts.Token).ConfigureAwait(false);
        }
        catch (FormatException ex)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            await Console.Error.WriteLineAsync("Run 'pay --help' for usage.").ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or RpcClientUnknownException
                                       or RpcResponseException or ArgumentException)
        {
            await Console.Error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }
}
