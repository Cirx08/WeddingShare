﻿using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using WeddingShare.BackgroundWorkers;
using WeddingShare.Configurations;
using WeddingShare.Helpers;
using WeddingShare.Helpers.Database;
using WeddingShare.Models.Database;

namespace WeddingShare
{
    public class Startup
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        public Startup(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            Configuration = configuration;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<Startup>();
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            var config = new ConfigHelper(new EnvironmentWrapper(), Configuration, _loggerFactory.CreateLogger<ConfigHelper>());
            
            services.AddDependencyInjectionConfiguration();
            services.AddDatabaseConfiguration(config);
            services.AddNotificationConfiguration(config);

            services.AddHostedService<DirectoryScanner>();
            services.AddHostedService<NotificationReport>();

            services.AddRazorPages();
            services.AddControllersWithViews().AddRazorRuntimeCompilation();

            services.AddLocalization(options =>
            {
                options.ResourcesPath = "Resources";
            });

            services.Configure<KestrelServerOptions>(options =>
            {
                options.Limits.MaxRequestBodySize = int.MaxValue;
            });

            services.Configure<FormOptions>(x =>
            {
                x.MultipartHeadersLengthLimit = Int32.MaxValue;
                x.MultipartBoundaryLengthLimit = Int32.MaxValue;
                x.MultipartBodyLengthLimit = Int64.MaxValue;
                x.ValueLengthLimit = Int32.MaxValue;
                x.BufferBodyLengthLimit = Int64.MaxValue;
                x.MemoryBufferThreshold = Int32.MaxValue;
            });

            services.Configure<RequestLocalizationOptions>(options => {
                var supportedCultures = new[]
                {
                    new CultureInfo("en-GB"),
                    new CultureInfo("fr-FR")
                };

                options.DefaultRequestCulture = new RequestCulture("en-GB");
                options.SupportedCultures = supportedCultures;
                options.SupportedUICultures = supportedCultures;
            });

            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
            {
                options.Cookie.HttpOnly = false;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);

                options.LoginPath = "/Admin/Login";
                options.AccessDeniedPath = $"/Error?Reason={ErrorCode.Unauthorized}";
                options.SlidingExpiration = true;
            });
            services.AddSession();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (!env.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            var config = new ConfigHelper(new EnvironmentWrapper(), Configuration, _loggerFactory.CreateLogger<ConfigHelper>());
            if (config.GetOrDefault("Settings", "Force_Https", false))
            { 
                app.UseHttpsRedirection();
            }

            app.UseStaticFiles();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRequestLocalization();
            app.UseSession();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller}/{action}/{id?}",
                    defaults: new { controller = "Home", action = "Index" });

                endpoints.MapRazorPages();
            });
        }
    }
}