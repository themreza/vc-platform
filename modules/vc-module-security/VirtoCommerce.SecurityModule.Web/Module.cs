using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VirtoCommerce.Platform.Core.Bus;
using VirtoCommerce.Platform.Core.ChangeLog;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Security.Events;
using VirtoCommerce.Platform.Core.Security.Search;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Platform.Security;
using VirtoCommerce.Platform.Security.Handlers;
using VirtoCommerce.Platform.Security.Repositories;
using VirtoCommerce.Platform.Security.Services;
using Microsoft.EntityFrameworkCore;
using VirtoCommerce.Platform.Core;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using VirtoCommerce.SecurityModule.Web.Authentication;
using AuthorizationOptions = VirtoCommerce.Platform.Core.Security.AuthorizationOptions;
using OpenIddict.Validation;
using AspNet.Security.OpenIdConnect.Primitives;
using VirtoCommerce.Platform.Security.Authorization;
using VirtoCommerce.SecurityModule.Web.Authorization;

namespace VirtoCommerce.SecurityModule.Web
{
    public class Module : IModule
    {
        public ManifestModuleInfo ModuleInfo { get; set; }

        public void Initialize(IServiceCollection services)
        {
            services.AddDbContext<SecurityDbContext>(options =>
            {
                //options.UseSqlServer(sp.GetService<IConfiguration>().GetConnectionString("VirtoCommerce"));
                options.UseInMemoryDatabase(nameof(SecurityDbContext));

                // Register the entity sets needed by OpenIddict.
                // Note: use the generic overload if you need
                // to replace the default OpenIddict entities.
                options.UseOpenIddict();
            });

            services.AddTransient<ISecurityRepository, SecurityRepository>();
            services.AddTransient<Func<ISecurityRepository>>(provider => () => provider.CreateScope().ServiceProvider.GetService<ISecurityRepository>());

            //services.AddScoped<IUserApiKeyService, UserApiKeyService>();
            //services.AddScoped<IUserApiKeySearchService, UserApiKeySearchService>();

            services.AddScoped<IUserNameResolver, HttpContextUserResolver>();
            services.AddSingleton<IPermissionsRegistrar, DefaultPermissionProvider>();
            services.AddScoped<IRoleSearchService, RoleSearchService>();
            //Register as singleton because this abstraction can be used as dependency in singleton services
            services.AddSingleton<IUserSearchService>(provider => new UserSearchService(provider.CreateScope().ServiceProvider.GetService<Func<UserManager<ApplicationUser>>>()));

            //Identity dependencies override
            services.TryAddScoped<RoleManager<Role>, CustomRoleManager>();
            services.TryAddScoped<UserManager<ApplicationUser>, CustomUserManager>();
            services.AddSingleton<Func<UserManager<ApplicationUser>>>(provider => () => provider.CreateScope().ServiceProvider.GetService<UserManager<ApplicationUser>>());
            services.AddSingleton<Func<SignInManager<ApplicationUser>>>(provider => () => provider.CreateScope().ServiceProvider.GetService<SignInManager<ApplicationUser>>());
            services.AddSingleton<IUserPasswordHasher, DefaultUserPasswordHasher>();
            //Use custom ClaimsPrincipalFactory to add system roles claims for user principal
            services.TryAddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, CustomUserClaimsPrincipalFactory>();

            //if (setupAction != null)
            //{
            //    services.Configure(setupAction);
            //}

            services.AddIdentity<ApplicationUser, Role>(options => options.Stores.MaxLengthForKeys = 128)
                    .AddEntityFrameworkStores<SecurityDbContext>()
                    .AddDefaultTokenProviders();

            services.Configure<IdentityOptions>(options =>
            {
                options.ClaimsIdentity.UserNameClaimType = OpenIdConnectConstants.Claims.Subject;
                options.ClaimsIdentity.UserIdClaimType = OpenIdConnectConstants.Claims.Name;
                options.ClaimsIdentity.RoleClaimType = OpenIdConnectConstants.Claims.Role;
            });

            // register the AuthorizationPolicyProvider which dynamically registers authorization policies for each permission defined in module manifest
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            //Platform authorization handler for policies based on permissions
            services.AddSingleton<IAuthorizationHandler, DefaultPermissionAuthorizationHandler>();
            // Default password validation service implementation
            services.AddScoped<IPasswordCheckService, PasswordCheckService>();

            var providerSnapshot = services.BuildServiceProvider();
            var configuration = providerSnapshot.GetService<IConfiguration>();

            services.AddOptions<AuthorizationOptions>().Bind(configuration.GetSection("Authorization")).ValidateDataAnnotations();

            var webHostEnvironment = providerSnapshot.GetService<IWebHostEnvironment>();

            AddOpenIdDict(services, configuration, webHostEnvironment);

        }

        public void PostInitialize(IApplicationBuilder app)
        {
            app.UseDefaultUsersAsync().GetAwaiter().GetResult();
        }

        public void Uninstall()
        {
            // do nothing in here
        }


        private void AddOpenIdDict(IServiceCollection services, IConfiguration configuration, IWebHostEnvironment webHostEnvironment)
        {
            var authorizationOptions = configuration.GetSection("Authorization").Get<AuthorizationOptions>();
            var platformOptions = configuration.GetSection("VirtoCommerce").Get<PlatformOptions>();

            services.AddOpenIddict()
                // Register the OpenIddict core services.
                .AddCore(options =>
                {
                    // Configure OpenIddict to use the EF Core stores/models.
                    options.UseEntityFrameworkCore()
                               .UseDbContext<SecurityDbContext>();
                })
                // Register the OpenIddict server handler.
                .AddServer(options =>
                {
                    // Register the ASP.NET Core MVC binder used by OpenIddict.
                    // Note: if you don't call this method, you won't be able to
                    // bind OpenIdConnectRequest or OpenIdConnectResponse parameters.
                    options.UseMvc();

                    // Enable the authorization, logout, token and userinfo endpoints.
                    options.EnableTokenEndpoint("/connect/token")
                        .EnableUserinfoEndpoint("/api/security/userinfo");

                    // Note: the Mvc.Client sample only uses the code flow and the password flow, but you
                    // can enable the other flows if you need to support implicit or client credentials.
                    options.AllowPasswordFlow()
                        .AllowRefreshTokenFlow()
                        .AllowClientCredentialsFlow();

                    options.SetRefreshTokenLifetime(authorizationOptions?.RefreshTokenLifeTime);
                    options.SetAccessTokenLifetime(authorizationOptions?.AccessTokenLifeTime);

                    options.AcceptAnonymousClients();

                    // Configure Openiddict to issues new refresh token for each token refresh request.
                    options.UseRollingTokens();

                    // Make the "client_id" parameter mandatory when sending a token request.
                    //options.RequireClientIdentification();

                    // When request caching is enabled, authorization and logout requests
                    // are stored in the distributed cache by OpenIddict and the user agent
                    // is redirected to the same page with a single parameter (request_id).
                    // This allows flowing large OpenID Connect requests even when using
                    // an external authentication provider like Google, Facebook or Twitter.
                    options.EnableRequestCaching();

                    options.DisableScopeValidation();

                    // During development or when you explicitly run the platform in production mode without https, need to disable the HTTPS requirement.
                    if (webHostEnvironment.IsDevelopment() || platformOptions.AllowInsecureHttp /*|| !Configuration.IsHttpsServerUrlSet()*/)
                    {
                        options.DisableHttpsRequirement();
                    }

                    // Note: to use JWT access tokens instead of the default
                    // encrypted format, the following lines are required:
                    options.UseJsonWebTokens();

                    var bytes = File.ReadAllBytes(configuration["Auth:PrivateKeyPath"]);
                    X509Certificate2 privateKey;
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        // https://github.com/dotnet/corefx/blob/release/2.2/Documentation/architecture/cross-platform-cryptography.md
                        // macOS cannot load certificate private keys without a keychain object, which requires writing to disk. Keychains are created automatically for PFX loading, and are deleted when no longer in use. Since the X509KeyStorageFlags.EphemeralKeySet option means that the private key should not be written to disk, asserting that flag on macOS results in a PlatformNotSupportedException.
                        privateKey = new X509Certificate2(bytes, configuration["Auth:PrivateKeyPassword"], X509KeyStorageFlags.MachineKeySet);
                    }
                    else
                    {
                        privateKey = new X509Certificate2(bytes, configuration["Auth:PrivateKeyPassword"], X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
                    }
                    options.AddSigningCertificate(privateKey);
                })
                // Register the OpenIddict validation handler.
                // Note: the OpenIddict validation handler is only compatible with the
                // default token format or with reference tokens and cannot be used with
                // JWT tokens. For JWT tokens, use the Microsoft JWT bearer handler.
                .AddValidation();
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = OpenIddictValidationDefaults.AuthenticationScheme;
            });
        }

    }
}