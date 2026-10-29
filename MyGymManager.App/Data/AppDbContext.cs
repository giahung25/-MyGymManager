namespace MyGymManager.Data;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MyGymManager.Core.Entities;

/// <summary>
/// DbContext Entity Framework Core 10 kết nối cơ sở dữ liệu SQLite cục bộ (phuc_gym.db).
/// Hỗ trợ lưu trữ List<float[]> (512-d ArcFace vectors) qua ValueConverter JSON.
/// </summary>
public class AppDbContext : DbContext
{
    public const string DefaultDbFile = "phuc_gym.db";

    public DbSet<Member> Members => Set<Member>();
    public DbSet<Package> Packages => Set<Package>();
    public DbSet<CheckInLog> CheckInLogs => Set<CheckInLog>();

    public AppDbContext() { }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={DefaultDbFile}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 2. Cấu hình bảng Members
        modelBuilder.Entity<Member>(entity =>
        {
            entity.ToTable("Members");
            entity.HasKey(m => m.Id);

            entity.Property(m => m.FullName)
                  .IsRequired()
                  .HasMaxLength(150);

            entity.Property(m => m.PhoneNumber)
                  .IsRequired()
                  .HasMaxLength(20);

            entity.HasIndex(m => m.PhoneNumber)
                  .IsUnique();

            entity.Property(m => m.AvatarPath)
                  .HasMaxLength(500);

            entity.Property(m => m.StartDate)
                  .IsRequired();

            entity.Property(m => m.EndDate)
                  .IsRequired();

            entity.Property(m => m.IsActive)
                  .IsRequired();

            entity.Property(m => m.CreatedAt)
                  .IsRequired();


            // Mối quan hệ Member -> Package (n:1)
            entity.HasOne(m => m.Package)
                  .WithMany(p => p.Members)
                  .HasForeignKey(m => m.PackageId)
                  .OnDelete(DeleteBehavior.SetNull);

            // Mối quan hệ Member -> CheckInLogs (1:n)
            entity.HasMany(m => m.CheckInLogs)
                  .WithOne(c => c.Member)
                  .HasForeignKey(c => c.MemberId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        // 3. Cấu hình bảng Packages
        modelBuilder.Entity<Package>(entity =>
        {
            entity.ToTable("Packages");
            entity.HasKey(p => p.Id);

            entity.Property(p => p.Name)
                  .IsRequired()
                  .HasMaxLength(100);

            entity.Property(p => p.DurationDays)
                  .IsRequired();

            entity.Property(p => p.Price)
                  .HasPrecision(18, 2);

            entity.Property(p => p.Description)
                  .HasMaxLength(500);
        });

        // 4. Cấu hình bảng CheckInLogs
        modelBuilder.Entity<CheckInLog>(entity =>
        {
            entity.ToTable("CheckInLogs");
            entity.HasKey(c => c.Id);

            entity.Property(c => c.CheckInTime)
                  .IsRequired();

            entity.Property(c => c.ConfidenceScore)
                  .IsRequired();
        });
    }
}
