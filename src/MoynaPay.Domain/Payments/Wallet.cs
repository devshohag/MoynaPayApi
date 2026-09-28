using MoynaPay.Domain.Common;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Domain.Payments;

using MoynaPay.Domain.Merchants;

/// <summary>A merchant's receiving wallet. Money lands here directly; MoynaPay only observes it.</summary>
public class Wallet : BaseEntity
{
    public PaymentMethod Method { get; set; }

    /// <summary>The receiving number shown to customers on the checkout page.</summary>
    public string Number { get; set; } = null!;

    public WalletAccountType AccountType { get; set; } = WalletAccountType.PersonalRetail;
    public string? Label { get; set; }

    /// <summary>
    /// The limit the provider has stated for this account, as the merchant reported it.
    /// Used for warnings as the merchant approaches it. MoynaPay never splits load across
    /// wallets to work around a limit.
    /// </summary>
    public decimal? DeclaredMonthlyLimit { get; set; }

    public bool IsActive { get; set; } = true;

    public Merchant Merchant { get; set; } = null!;
    public ICollection<Device> Devices { get; set; } = new List<Device>();
}
