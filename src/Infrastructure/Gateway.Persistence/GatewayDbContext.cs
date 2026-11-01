using Gateway.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Persistence;

public class GatewayDbContext : DbContext
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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
            // Strict uniqueness on actual blockchain event index
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
            // Database-level enforcement against replay attacks
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
    }
}