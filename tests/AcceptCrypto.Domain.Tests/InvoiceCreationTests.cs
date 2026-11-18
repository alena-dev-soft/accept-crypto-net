using AcceptCrypto.Domain.Invoices;
using AcceptCrypto.Domain.Money;

namespace AcceptCrypto.Domain.Tests;

public class InvoiceCreationTests
{
    private readonly TestKit _kit = new();

    private Invoice Create(
        string amount = "10",
        string orderId = "order-1",
        TimeSpan? lifetime = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? idempotencyKey = null,
        long index = 7,
        string address = TestKit.DepositAddress) =>
        Invoice.Create(new NewInvoice(UsdcAmount.Parse(amount), orderId, address, index, metadata, idempotencyKey, lifetime), _kit.Policy, _kit.Clock);

    [Fact]
    public void Create_SetsFieldsAndStartsPending()
    {
        var invoice = Create(metadata: new Dictionary<string, string> { ["sku"] = "A-1" }, idempotencyKey: "key-1");

        Assert.Equal(InvoiceStatus.Pending, invoice.Status);
        Assert.Equal(UsdcAmount.Parse("10"), invoice.Amount);
        Assert.Equal("order-1", invoice.ExternalOrderId);
        Assert.Equal("A-1", invoice.Metadata["sku"]);
        Assert.Equal("key-1", invoice.IdempotencyKey);
        Assert.Equal(TestKit.DepositAddress, invoice.DepositAddress);
        Assert.Equal(7, invoice.DerivationIndex);
        Assert.Equal(TestKit.Start, invoice.CreatedAt);
        Assert.Equal(TestKit.Start, invoice.UpdatedAt);
        Assert.Empty(invoice.Transfers);
        Assert.Equal(invoice.Amount, invoice.RemainingAmount);
        Assert.NotEqual(Guid.Empty, invoice.Id);
        Assert.Equal(7, invoice.Id.Version);
    }

    [Fact]
    public void Create_NormalizesDepositAddress() =>
        Assert.Equal("0xabcdef0000000000000000000000000000000000", Create(address: "0xABCDEF0000000000000000000000000000000000").DepositAddress);

    [Fact]
    public void Deadline_DefaultLifetime()
    {
        _kit.Clock.Advance(TimeSpan.FromHours(3));
        var invoice = Create();
        Assert.Equal(TestKit.Start + TimeSpan.FromHours(3) + _kit.Policy.DefaultLifetime, invoice.ExpiresAt);
    }

    [Theory]
    [InlineData(1)] // = MinLifetime
    [InlineData(90)]
    [InlineData(7 * 24 * 60)] // = MaxLifetime
    public void Deadline_OverrideWithinLimits(int minutes)
    {
        var invoice = Create(lifetime: TimeSpan.FromMinutes(minutes));
        Assert.Equal(TestKit.Start + TimeSpan.FromMinutes(minutes), invoice.ExpiresAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(7 * 24 * 3600 + 1)]
    public void Deadline_OverrideOutsideLimits_Rejected(int seconds)
    {
        var ex = Assert.Throws<DomainException>(() => Create(lifetime: TimeSpan.FromSeconds(seconds)));
        Assert.Equal(DomainErrorCodes.LifetimeOutOfRange, ex.Code);
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("1000000")]
    public void Amount_AtLimits_Accepted(string amount) => Assert.Equal(UsdcAmount.Parse(amount), Create(amount).Amount);

    [Theory]
    [InlineData("0")]
    [InlineData("0.009999")]
    [InlineData("1000000.000001")]
    public void Amount_OutsideLimits_Rejected(string amount)
    {
        var ex = Assert.Throws<DomainException>(() => Create(amount));
        Assert.Equal(DomainErrorCodes.AmountOutOfRange, ex.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExternalOrderId_Required(string orderId) =>
        Assert.Equal(DomainErrorCodes.InvalidExternalOrderId, Assert.Throws<DomainException>(() => Create(orderId: orderId)).Code);

    [Fact]
    public void ExternalOrderId_TooLong_Rejected() =>
        Assert.Equal(DomainErrorCodes.InvalidExternalOrderId, Assert.Throws<DomainException>(() => Create(orderId: new string('x', 201))).Code);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void IdempotencyKey_Blank_Rejected(string key) =>
        Assert.Equal(DomainErrorCodes.InvalidIdempotencyKey, Assert.Throws<DomainException>(() => Create(idempotencyKey: key)).Code);

    [Fact]
    public void Metadata_Limits()
    {
        var tooMany = Enumerable.Range(0, 21).ToDictionary(i => $"k{i}", i => "v");
        Assert.Equal(DomainErrorCodes.InvalidMetadata, Assert.Throws<DomainException>(() => Create(metadata: tooMany)).Code);
        Assert.Throws<DomainException>(() => Create(metadata: new Dictionary<string, string> { [new string('k', 41)] = "v" }));
        Assert.Throws<DomainException>(() => Create(metadata: new Dictionary<string, string> { ["k"] = new string('v', 501) }));
        Assert.Throws<DomainException>(() => Create(metadata: new Dictionary<string, string> { [" "] = "v" }));
        Assert.Empty(Create(metadata: null).Metadata);
    }

    [Fact]
    public void Metadata_IsCopied()
    {
        var source = new Dictionary<string, string> { ["a"] = "1" };
        var invoice = Create(metadata: source);
        source["a"] = "changed";
        Assert.Equal("1", invoice.Metadata["a"]);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(2147483648L)]
    public void DerivationIndex_MustBeNonHardened(long index) =>
        Assert.Equal(DomainErrorCodes.InvalidChainData, Assert.Throws<DomainException>(() => Create(index: index)).Code);

    [Fact]
    public void DepositAddress_Malformed_Rejected() =>
        Assert.Equal(DomainErrorCodes.InvalidChainData, Assert.Throws<DomainException>(() => Create(address: "0x12")).Code);

    [Fact]
    public void PublicToken_IsUnguessable()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => Create().PublicToken).ToList();
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, t =>
        {
            Assert.Equal(43, t.Length); // 256 bits, base64url without padding
            Assert.Matches("^[A-Za-z0-9_-]+$", t);
        });
    }

    [Fact]
    public void Policy_Defaults_AreValid_AndInconsistentPolicyRejected()
    {
        new InvoicePolicy().EnsureValid();
        Assert.Throws<InvalidOperationException>(() => new InvoicePolicy { MinAmount = UsdcAmount.Zero }.EnsureValid());
        Assert.Throws<InvalidOperationException>(() => new InvoicePolicy { MinAmount = UsdcAmount.Parse("5"), MaxAmount = UsdcAmount.Parse("1") }.EnsureValid());
        Assert.Throws<InvalidOperationException>(() => new InvoicePolicy { DefaultLifetime = TimeSpan.FromDays(30) }.EnsureValid());
        Assert.Throws<InvalidOperationException>(() => new InvoicePolicy { MinLifetime = TimeSpan.Zero }.EnsureValid());
    }
}
