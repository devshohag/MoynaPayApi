using System.Security.Cryptography;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Domain.Merchants;

namespace MoynaPay.Application.Merchants;

public sealed class MerchantService(
    IMerchantStore merchants, ISecretProtector secrets, IClock clock)
{
    public async Task<CreateMerchantResult> CreateAsync(CreateMerchantCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var name = command.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new CreateMerchantResult(CreateMerchantOutcome.Invalid, null, "Merchant name is required.");
        }

        var msisdn = Msisdn.Normalise(command.Msisdn);
        if (msisdn is null)
        {
            return new CreateMerchantResult(CreateMerchantOutcome.Invalid, null, "A valid owner phone is required.");
        }

        var now = clock.UtcNow;
        var merchantId = Guid.CreateVersion7();

        var merchant = new Merchant
        {
            Id = merchantId,
            TenantId = merchantId,
            Name = name,
            Msisdn = msisdn,
            Status = MerchantStatus.Trial,
            TimeZone = string.IsNullOrWhiteSpace(command.TimeZone)
                ? "Asia/Dhaka"
                : command.TimeZone.Trim(),
            Address = BlankToNull(command.Address),
            SupportMsisdn = Msisdn.Normalise(command.SupportMsisdn),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var subscription = new Subscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = merchantId,
            Calls = command.Calls,
            Payments = command.Payments,
            Courier = command.Courier,
            Plan = string.IsNullOrWhiteSpace(command.Plan) ? "trial" : command.Plan.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await merchants.SaveMerchantAsync(merchant, subscription, ct).ConfigureAwait(false);

        return new CreateMerchantResult(CreateMerchantOutcome.Created, merchant, null);
    }

    public async Task<IssueApiKeyResult> IssueKeyAsync(Guid merchantId, string? label,
        bool bootstrapOnly = false, CancellationToken ct = default)
    {
        var merchant = await merchants.FindAsync(merchantId, ct).ConfigureAwait(false);
        if (merchant is null)
        {
            return new IssueApiKeyResult(IssueApiKeyOutcome.NoMerchant, null, null,
                "Merchant not found.");
        }

        if (bootstrapOnly && await merchants.HasAnyCredentialAsync(merchantId, ct).ConfigureAwait(false))
        {
            return new IssueApiKeyResult(IssueApiKeyOutcome.Refused, null, null,
                "This merchant already has an API key.");
        }

        var now = clock.UtcNow;
        var secret = MerchantSecrets.NewSecret();
        var cipher = secrets.Protect(secret, out var keyRingId);
        var credential = new ApiCredential
        {
            Id = Guid.CreateVersion7(),
            TenantId = merchantId,
            KeyId = NewKeyId(),
            SecretCipher = cipher,
            KeyRingId = keyRingId,
            Label = string.IsNullOrWhiteSpace(label) ? "default" : label.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await merchants.SaveCredentialAsync(credential, ct).ConfigureAwait(false);

        return new IssueApiKeyResult(IssueApiKeyOutcome.Issued, credential, secret, null);
    }

    public Task<IReadOnlyList<ApiCredential>> ListKeysAsync(Guid merchantId, CancellationToken ct = default) =>
        merchants.ListCredentialsAsync(merchantId, ct);

    public async Task<bool> RevokeKeyAsync(Guid merchantId, string keyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyId)) return false;

        return await merchants.RevokeCredentialAsync(
            merchantId, keyId.Trim(), clock.UtcNow, ct).ConfigureAwait(false);
    }

    private static string NewKeyId()
    {
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        return "mp_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record CreateMerchantCommand
{
    public string? Name { get; init; }
    public string? Msisdn { get; init; }
    public string? TimeZone { get; init; }
    public string? Address { get; init; }
    public string? SupportMsisdn { get; init; }
    public bool Calls { get; init; }
    public bool Payments { get; init; }
    public bool Courier { get; init; }
    public string? Plan { get; init; }
}

public enum CreateMerchantOutcome
{
    Created,
    Invalid,
}

public sealed record CreateMerchantResult(
    CreateMerchantOutcome Outcome,
    Merchant? Merchant,
    string? Reason);

public enum IssueApiKeyOutcome
{
    Issued,
    NoMerchant,
    Refused,
}

public sealed record IssueApiKeyResult(
    IssueApiKeyOutcome Outcome,
    ApiCredential? Credential,
    string? Secret,
    string? Reason);
