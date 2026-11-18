using AcceptCrypto.Domain.Chain;
using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Domain.Tests;

public class ChainTransferTests
{
    private static ChainTransfer Make(string? txHash = null, string? to = null, string amount = "1", int logIndex = 0, long block = 1) =>
        new(txHash ?? TestKit.Hash(1), logIndex, block, TestKit.Hash(2), TestKit.Start, TestKit.PayerAddress, to ?? TestKit.DepositAddress, UsdcAmount.Parse(amount));

    [Fact]
    public void NormalizesHexToLowerCase()
    {
        var t = Make(txHash: "0X" + new string('A', 64), to: "0xABCDEFabcdef0000000000000000000000000000");
        Assert.Equal("0x" + new string('a', 64), t.Id.TxHash);
        Assert.Equal("0xabcdefabcdef0000000000000000000000000000", t.To);
    }

    [Theory]
    [InlineData("0x123")]
    [InlineData("1111111111111111111111111111111111111111")]
    [InlineData("0x111111111111111111111111111111111111111g")]
    [InlineData("0x11111111111111111111111111111111111111111")]
    public void RejectsMalformedAddress(string to)
    {
        var ex = Assert.Throws<DomainException>(() => Make(to: to));
        Assert.Equal(DomainErrorCodes.InvalidChainData, ex.Code);
    }

    [Fact]
    public void RejectsMalformedHash_ZeroAmount_NegativeNumbers()
    {
        Assert.Throws<DomainException>(() => Make(txHash: "0xabc"));
        Assert.Throws<DomainException>(() => Make(amount: "0"));
        Assert.Throws<DomainException>(() => Make(logIndex: -1));
        Assert.Throws<DomainException>(() => Make(block: -1));
    }

    [Fact]
    public void TransferId_EqualityByHashAndLogIndex()
    {
        Assert.Equal(new TransferId(TestKit.Hash(1), 2), Make(logIndex: 2).Id);
        Assert.NotEqual(new TransferId(TestKit.Hash(1), 3), Make(logIndex: 2).Id);
    }
}
