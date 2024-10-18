using Microsoft.EntityFrameworkCore;
using OrderVerificationAPI.Models;

namespace OrderVerificationAPI
{
    public class AppDbContext : DbContext
    {
        public DbSet<Order> Orders { get; set; }

        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        public DbSet<Branch> Branches { get; set; }

        //protected override void OnModelCreating(ModelBuilder modelBuilder)
        //{
        //    base.OnModelCreating(modelBuilder);
        //    modelBuilder.Entity<Order>().HasIndex(o => o.OrderNumber).IsUnique();
        //}
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>()
                .Property(o => o.Amount)
                .HasColumnType("decimal(18,2)"); // Specify precision and scale

            base.OnModelCreating(modelBuilder);
        }

    }

}
