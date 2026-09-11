using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MapStitcher.Database
{
    // ApplicationDbContext

    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public DbSet<Project> Projects => Set<Project>();
        public DbSet<SurveySheet> SurveySheets => Set<SurveySheet>();
        public DbSet<TiePoint> TiePoints => Set<TiePoint>();
        public DbSet<SheetBoundary> SheetBoundaries => Set<SheetBoundary>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Project>(e =>
            {
                e.HasKey(p => p.ProjectID);
                e.Property(p => p.Name).IsRequired().HasMaxLength(200);
                e.Property(p => p.District).IsRequired().HasMaxLength(100);
                e.Property(p => p.Taluka).IsRequired().HasMaxLength(100);
                e.Property(p => p.Village).IsRequired().HasMaxLength(100);
            });

            modelBuilder.Entity<SurveySheet>(e =>
            {
                e.HasKey(s => s.SheetID);
                e.Property(s => s.SheetNumber).IsRequired().HasMaxLength(50);
                e.Property(s => s.LaghuReferenceNumber).HasMaxLength(20);
                e.Property(s => s.FilePath).IsRequired().HasMaxLength(500);
                e.Property(s => s.DwgVersion).HasMaxLength(10);
                e.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
                e.Property(s => s.FailureReason).HasMaxLength(200);
                e.HasIndex(s => s.LaghuReferenceNumber);
                e.Property(s => s.FileHash).HasMaxLength(64);
                e.HasOne(s => s.Project)
                 .WithMany(p => p.SurveySheets)
                 .HasForeignKey(s => s.ProjectID)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<TiePoint>(e =>
            {
                e.HasKey(t => t.PointID);
                e.Property(t => t.PointLabel).HasMaxLength(20);
                e.HasIndex(t => t.PointLabel);

                e.HasOne(t => t.SurveySheet)
                 .WithMany(s => s.TiePoints)
                 .HasForeignKey(t => t.SheetID)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<SheetBoundary>(e =>
            {
                e.HasKey(b => b.BoundaryID);
                e.Property(b => b.PlotLabel).HasMaxLength(50);
                e.Property(b => b.Geometry).HasColumnType("geometry");

                e.HasOne(b => b.SurveySheet)
                 .WithMany(s => s.Boundaries)
                 .HasForeignKey(b => b.SheetID)
                 .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}