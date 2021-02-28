﻿using System;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SS14.Auth.Shared.Data
{
    public class ApplicationDbContext : IdentityDbContext<SpaceUser, SpaceRole, Guid>, IDataProtectionKeyContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (_initializedWithoutOptions)
            {
                optionsBuilder.UseNpgsql("foobar");
            }
        }

        private readonly bool _initializedWithoutOptions;

        public ApplicationDbContext()
        {
            _initializedWithoutOptions = true;
        }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<SpaceUser>()
                .Ignore(p => p.PhoneNumber)
                .Ignore(p => p.PhoneNumberConfirmed)
                // 2FA disabled for now because I'd have to modify the launcher.
                .Ignore(p => p.TwoFactorEnabled)
                // I don't (currently) care about any of this lockout stuff.
                .Ignore(p => p.LockoutEnd)
                .Ignore(p => p.LockoutEnabled)
                .Ignore(p => p.AccessFailedCount);

            builder.Entity<LoginSession>()
                .HasIndex(p => p.Token)
                .IsUnique();

            builder.Entity<AuthHash>()
                .HasIndex(p => new {p.Hash, p.SpaceUserId})
                .IsUnique();

            builder.Entity<BurnerEmail>()
                .HasIndex(p => new {p.Domain})
                .IsUnique();

            builder.Entity<Patron>()
                .HasIndex(p => p.PatreonId)
                .IsUnique();
            
            builder.Entity<Patron>()
                .HasIndex(p => p.SpaceUserId)
                .IsUnique();
        }

        public DbSet<LoginSession> ActiveSessions { get; set; }
        public DbSet<AuthHash> AuthHashes { get; set; }
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }
        public DbSet<BurnerEmail> BurnerEmails { get; set; }
        public DbSet<Patron> Patrons { get; set; }
        public DbSet<PatreonWebhookLog> PatreonWebhookLogs { get; set; }
    }
}