using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using IdentityServer4;
using IdentityServer4.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Prometheus;
using Serilog;
using SS14.Auth.Shared;
using SS14.Auth.Shared.Config;
using SS14.Auth.Shared.Data;
using SS14.ServerHub.Shared.Data;
using SS14.Web.Data;
using SS14.Web.HCaptcha;
using SS14.WebEverythingShared;

namespace SS14.Web;

public class Startup
{
    public Startup(IConfiguration configuration, IWebHostEnvironment environment)
    {
        Configuration = configuration;
        Environment = environment;
    }

    public IConfiguration Configuration { get; }
    public IWebHostEnvironment Environment { get; }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<HubAuditLogManager>();

        services.Configure<AccountOptions>(Configuration.GetSection("Account"));
        HCaptchaService.RegisterServices(services, Configuration);

        services.AddDatabaseDeveloperPageExceptionFilter();
        StartupHelpers.AddShared(services, Configuration);

        services.AddDbContext<HubDbContext>(options =>
        {
            var connectionString = Configuration.GetConnectionString("HubConnection") ?? throw new InvalidOperationException("Must set HubConnection");
            options.UseNpgsql(connectionString);
        });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                AuthConstants.PolicyAnyHubAdmin,
                policy => policy.RequireRole(AuthConstants.RoleSysAdmin, AuthConstants.RoleServerHubAdmin)
            );

            options.AddPolicy(
                AuthConstants.PolicySysAdmin,
                policy => policy.RequireRole(AuthConstants.RoleSysAdmin)
            );

            options.AddPolicy(
                AuthConstants.PolicyServerHubAdmin,
                policy => policy.RequireRole(AuthConstants.RoleServerHubAdmin)
            );
        });

        services.AddMvc()
            .AddRazorPagesOptions(options =>
            {
                options.Conventions.AuthorizeAreaFolder("Identity", "/Account/Manage");
                options.Conventions.AuthorizeAreaPage("Identity", "/Account/Logout");
                options.Conventions.AuthorizeAreaFolder("Admin", "/", AuthConstants.PolicyAnyHubAdmin);
                options.Conventions.AuthorizeAreaFolder("Admin", "/Clients", AuthConstants.PolicySysAdmin);
                options.Conventions.AuthorizeAreaFolder("Admin", "/Users", AuthConstants.PolicySysAdmin);
                options.Conventions.AuthorizeAreaFolder("Admin", "/Servers", AuthConstants.PolicyServerHubAdmin);
            });

        services.ConfigureApplicationCookie(options =>
        {
            options.LoginPath = $"/Identity/Account/Login";
            options.LogoutPath = $"/Identity/Account/Logout";
            options.AccessDeniedPath = $"/Identity/Account/AccessDenied";
        });

        services.AddControllersWithViews();
        services.AddRazorPages();

        services.AddScoped<PatreonConnectionHandler>();

        var patreonSection = Configuration.GetSection("Patreon");
        var patreonCfg = patreonSection.Get<PatreonConfiguration>();

        services.AddAuthentication(options => options.DefaultScheme = IdentityConstants.ApplicationScheme);

        if (patreonCfg?.ClientId != null && patreonCfg.ClientSecret != null)
        {
            services.AddAuthentication()
                // Rider is dumb that null is valid.
                // It disables Patreon as an external login.
                .AddPatreon("Patreon", null!, options =>
                {
                    // Patreon docs lied you don't need this to see memberships to your own campaign.
                    // options.Scope.Add("identity.memberships");
                    options.Includes.Add("memberships.currently_entitled_tiers");
                    options.ClientId = patreonCfg.ClientId;
                    options.ClientSecret = patreonCfg.ClientSecret;

                    options.Events.OnCreatingTicket += context =>
                    {
                        var handler = context.HttpContext.RequestServices.GetService<PatreonConnectionHandler>();
                        return handler!.HookCreatingTicket(context);
                    };

                    options.Events.OnTicketReceived += context =>
                    {
                        var handler = context.HttpContext.RequestServices.GetService<PatreonConnectionHandler>();
                        return handler!.HookReceivedTicket(context);
                    };
                });
        }


        var builder = services.AddIdentityServer(options =>
            {
                options.UserInteraction.ConsentUrl = "/Identity/Account/Consent";
            })
            .AddAspNetIdentity<SpaceUser>()
            .AddOperationalStore<ApplicationDbContext>()
            .AddConfigurationStore<ApplicationDbContext>()
            .AddInMemoryIdentityResources(new IdentityResource[]
            {
                new IdentityResources.OpenId(),
                new IdentityResources.Profile(),
                new IdentityResources.Email(),
            });

        var keyPath = Configuration.GetValue<string>("Is4SigningKeyPath");
        if (keyPath == null)
        {
            if (Environment.IsDevelopment())
            {
                Log.Debug("Using developer signing credentials");
                builder.AddDeveloperSigningCredential();
            }
            else
            {
                throw new Exception("No key specified for IS4!");
            }
        }
        else
        {
            var keyPem = File.ReadAllText(keyPath);
            var key = ECDsa.Create();
            key.ImportFromPem(keyPem);

            builder.AddSigningCredential(
                new ECDsaSecurityKey(key),
                IdentityServerConstants.ECDsaSigningAlgorithm.ES256);
        }

        var keyPathRsa = Configuration.GetValue<string>("Is4SigningKeyPathRsa");
        if (keyPathRsa != null)
        {
            var keyPem = File.ReadAllText(keyPathRsa);
            var key = RSA.Create();
            key.ImportFromPem(keyPem);

            builder.AddSigningCredential(
                new RsaSecurityKey(key),
                IdentityServerConstants.RsaSigningAlgorithm.PS256);
            builder.AddSigningCredential(
                new RsaSecurityKey(key),
                IdentityServerConstants.RsaSigningAlgorithm.RS256);
        }

        services.AddScoped<PersonalDataCollector>();
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms to {ClientAddress}";
            options.EnrichDiagnosticContext = (context, httpContext) =>
            {
                context.Set("ClientAddress", httpContext.Connection.RemoteIpAddress);
            };
        });

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
            app.UseMigrationsEndPoint();
        }
        else
        {
            app.UseExceptionHandler("/Home/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        MoreStartupHelpers.AddForwardedSupport(app, Configuration);

        var pathBase = Configuration.GetValue<string>("PathBase");
        if (!string.IsNullOrEmpty(pathBase))
        {
            app.UsePathBase(pathBase);
        }

        //app.UseHttpsRedirection();
        app.UseStaticFiles();

        app.UseRouting();

        app.UseHttpMetrics();

        app.UseCookiePolicy(new CookiePolicyOptions()
        {
            Secure = CookieSecurePolicy.Always,
            MinimumSameSitePolicy = SameSiteMode.Lax,
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllerRoute(
                name: "default",
                pattern: "{controller=Home}/{action=Index}/{id?}");
            endpoints.MapRazorPages();
            endpoints.MapMetrics();
        });

        app.UseIdentityServer();
    }
}
