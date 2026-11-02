using Gateway.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Persistence;

public class GatewayDbContext : IdentityDbContext<ApplicationUser>
{
    public GatewayDbContext(DbContextOptions<GatewayDbContext> options) : base(options) { }

    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<DerivedAddress> DerivedAddresses => Set<DerivedAddress>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<PaymentTransfer> PaymentTransfers => Set<PaymentTransfer>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<MerchantNonce> MerchantNonces => Set<MerchantNonce>();
    public DbSet<BlockchainScanCursor> BlockchainScanCursors => Set<BlockchainScanCursor>();
    public DbSet<SweepTask> SweepTasks => Set<SweepTask>();
    public DbSet<MerchantIdempotencyKey> MerchantIdempotencyKeys => Set<MerchantIdempotencyKey>();
    public DbSet<MerchantClaimToken> MerchantClaimTokens => Set<MerchantClaimToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ApplicationUser>(b =>
        {
            b.HasOne<Merchant>()
                .WithMany() // Merchant does not have a navigation collection of Users
                .HasForeignKey(u => u.MerchantId)
                .HasPrincipalKey(m => m.Id)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MerchantClaimToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.HashedToken).IsUnique();
            b.HasIndex(t => new { t.MerchantId, t.IsUsed, t.ExpiresAtUtc });
        });

        modelBuilder.Entity<Merchant>(b =>
        {
            b.HasKey(m => m.Id);
            b.HasIndex(m => m.ApiKeyHash).IsUnique();
        });
        modelBuilder.Entity<Wallet>(b =>
        {
            b.HasKey(w => w.Id);
            b.HasIndex(w => new { w.MerchantId, w.Network }).IsUnique();
        });
        modelBuilder.Entity<DerivedAddress>(b =>
        {
            b.HasKey(a => a.Id);
            b.HasIndex(a => new { a.WalletId, a.AddressIndex }).IsUnique();
            b.HasIndex(a => new { a.Network, a.Address }).IsUnique();
        });
        modelBuilder.Entity<Invoice>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.FiatAmount).HasPrecision(18, 2);
            b.Property(i => i.ExchangeRate).HasPrecision(18, 6);
            b.HasIndex(i => i.CheckoutToken).IsUnique();
            b.HasIndex(i => new { i.Status, i.ExpiresAtUtc });
        });
        modelBuilder.Entity<PaymentTransfer>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => new { t.Network, t.TransactionHash, t.TransferIndex }).IsUnique();
        });
        modelBuilder.Entity<OutboxEvent>(b =>
        {
            b.HasKey(o => o.Id);
            b.HasIndex(o => new { o.IsProcessed, o.NextAttemptAtUtc });
        });
        modelBuilder.Entity<MerchantNonce>(b =>
        {
            b.HasKey(n => n.Id);
            b.HasIndex(n => new { n.MerchantId, n.Nonce }).IsUnique();
            b.HasIndex(n => n.ExpiresAtUtc);
        });
        modelBuilder.Entity<BlockchainScanCursor>(b =>
        {
            b.HasKey(c => new { c.Network, c.Address });
        });
        modelBuilder.Entity<SweepTask>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasIndex(s => new { s.MerchantId, s.Status });
            b.HasIndex(s => s.DerivedAddressId);
        });
        modelBuilder.Entity<MerchantIdempotencyKey>(b =>
        {
            b.HasKey(k => k.Id);
            b.HasIndex(k => new { k.MerchantId, k.Key }).IsUnique();
            b.HasIndex(k => k.ExpiresAtUtc);
        });
    }
}