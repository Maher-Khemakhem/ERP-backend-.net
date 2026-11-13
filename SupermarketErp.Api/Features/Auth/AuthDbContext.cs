using Microsoft.EntityFrameworkCore;

namespace SupermarketErp.Api.Features.Auth;

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    internal DbSet<AuthUser> Users => Set<AuthUser>();
    internal DbSet<AuthUserRole> UserRoles => Set<AuthUserRole>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var user = modelBuilder.Entity<AuthUser>();
        user.ToTable("app_user", "dbo");
        user.HasKey(x => x.Id);
        user.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        user.Property(x => x.Username).HasColumnName("username").HasMaxLength(80).IsUnicode(false).IsRequired();
        user.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(120).IsUnicode(false).IsRequired();
        user.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255).IsUnicode(false).IsRequired();
        user.Property(x => x.Enabled).HasColumnName("enabled").IsRequired();
        user.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        user.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        user.HasIndex(x => x.Username).IsUnique();

        var role = modelBuilder.Entity<AuthUserRole>();
        role.ToTable("app_user_roles", "dbo");
        role.HasKey(x => new { x.UserId, x.Role });
        role.Property(x => x.UserId).HasColumnName("user_id");
        role.Property(x => x.Role).HasColumnName("role").HasMaxLength(40).IsUnicode(false).IsRequired();
        role.HasOne(x => x.User)
            .WithMany(x => x.Roles)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
