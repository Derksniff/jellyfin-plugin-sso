using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Helpers;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SSO_Auth.Api;

/// <summary>
/// The sso api controller.
/// </summary>
[ApiController]
[Route("[controller]")]
public class SSOController : ControllerBase
{
    private const string UnverifiedLinkingError = "A Jellyfin account with this username already exists and is not linked to this SSO provider. Link it from the self-service linking page while signed in to that account, or ask an administrator to enable unverified linking for this provider.";

    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IAuthorizationContext _authContext;
    private readonly ILogger<SSOController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICryptoProvider _cryptoProvider;
    private readonly IProviderManager _providerManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private static readonly ConcurrentDictionary<string, TimedAuthorizeState> StateManager = new ConcurrentDictionary<string, TimedAuthorizeState>();

    // Caches the OIDC discovery document (endpoints + signing keys) per provider so we don't
    // re-run discovery on every login. OidcClient otherwise fetches .well-known + JWKS on each
    // request, which adds multiple round trips to the IdP per sign-in.
    private static readonly ConcurrentDictionary<string, CachedProviderInfo> DiscoveryCache = new ConcurrentDictionary<string, CachedProviderInfo>();
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromMinutes(15);

    // Tracks SAML assertion IDs already consumed by SamlAuth (keyed "provider|assertionId" -> assertion
    // expiry), so a captured, still-valid assertion cannot be replayed to authenticate a second time.
    private static readonly ConcurrentDictionary<string, DateTime> SamlReplayCache = new ConcurrentDictionary<string, DateTime>();

    // Serializes canonical-link read-modify-write so concurrent logins on the same provider cannot
    // corrupt the (non-thread-safe) dictionary or clobber each other's writes.
    private static readonly object CanonicalLinkLock = new object();

    // How long an in-flight OpenID login state is kept. Long enough to survive a slow IdP step
    // (password + MFA), but bounded so a state cannot be matched indefinitely.
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Initializes a new instance of the <see cref="SSOController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{SSOController}"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="cryptoProvider">Instance of the <see cref="ICryptoProvider"/> interface.</param>
    /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    public SSOController(
        ILogger<SSOController> logger,
        ILoggerFactory loggerFactory,
        ISessionManager sessionManager,
        IUserManager userManager,
        IAuthorizationContext authContext,
        ICryptoProvider cryptoProvider,
        IProviderManager providerManager,
        IHttpClientFactory httpClientFactory,
        IServerConfigurationManager serverConfigurationManager)
    {
        _sessionManager = sessionManager;
        _userManager = userManager;
        _authContext = authContext;
        _cryptoProvider = cryptoProvider;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _providerManager = providerManager;
        _serverConfigurationManager = serverConfigurationManager;
        _httpClientFactory = httpClientFactory;
        _logger.LogInformation("SSO Controller initialized");
    }

    /// <summary>
    /// The GET endpoint for OpenID provider to callback to. Returns a webpage that parses client data and completes auth.
    /// </summary>
    /// <param name="provider">The ID of the provider which will use the callback information.</param>
    /// <param name="state">The current request state.</param>
    /// <returns>A webpage that will complete the client-side flow.</returns>
    // Actually a GET: https://github.com/IdentityModel/IdentityModel.OidcClient/issues/325
    [HttpGet("OID/r/{provider}")]
    [HttpGet("OID/redirect/{provider}")]
    public ActionResult OidPost(
        [FromRoute] string provider,
        [FromQuery] string state) // Although this is a GET function, this function is called `Post` for consistency with SAML
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            if (string.IsNullOrEmpty(state))
            {
                return BadRequest("Missing state");
            }

            if (!StateManager.TryGetValue(state, out var timedState))
            {
                return BadRequest("Invalid or expired state");
            }

            // Defer the provider token exchange until the client POSTs back to
            // /OID/Auth (or /Link): stash the raw callback query, then return the
            // loading page immediately. The slow provider round-trip then runs
            // while the user is already seeing "Connecting to your account..."
            // instead of a blank browser tab.
            timedState.CallbackQuery = Request.QueryString.Value;

            return Content(WebResponse.Generator(data: state, provider: provider, baseUrl: GetRequestBase(config.SchemeOverride, config.PortOverride), mode: "OID", isLinking: timedState.IsLinking), MediaTypeNames.Text.Html);
        }

        return BadRequest("No matching provider found");
    }

    // Sets the plugin's User-Agent on an outbound HTTP client.
    private static void AddPluginUserAgent(HttpClient client)
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jellyfin-Plugin-SSO-Auth +{fvi.FileVersion} (https://github.com/9p4/jellyfin-plugin-sso)");
    }

    // Builds an OidcClient (plus the options and cache metadata the caller needs to persist discovery)
    // for the given provider and redirect URI, applying this plugin's discovery-policy security
    // settings. Shared by the login challenge and the deferred callback exchange so the two paths
    // cannot drift apart.
    private (OidcClient Client, OidcClientOptions Options, string CacheKey, bool CacheHit) BuildOidcClient(string provider, OidConfig config, string redirectUri)
    {
        var scopes = config.OidScopes ?? Array.Empty<string>();
        var options = new OidcClientOptions
        {
            Authority = config.OidEndpoint?.Trim(),
            ClientId = config.OidClientId?.Trim(),
            ClientSecret = config.OidSecret?.Trim(),
            RedirectUri = redirectUri,
            Scope = string.Join(" ", scopes.Prepend("openid profile")),
            DisablePushedAuthorization = config.DisablePushedAuthorization,
            LoggerFactory = _loggerFactory,
            LoadProfile = !config.DoNotLoadProfile,
            HttpClientFactory = o =>
            {
                var client = _httpClientFactory.CreateClient();
                AddPluginUserAgent(client);
                return client;
            }
        };
        var oidEndpointUri = new Uri(config.OidEndpoint?.Trim());
        options.Policy.Discovery.AdditionalEndpointBaseAddresses.Add(oidEndpointUri.GetLeftPart(UriPartial.Authority));
        options.Policy.Discovery.ValidateEndpoints = !config.DoNotValidateEndpoints; // For Google and other providers with different endpoints
        options.Policy.Discovery.RequireHttps = !config.DisableHttps;
        options.Policy.Discovery.ValidateIssuerName = !config.DoNotValidateIssuerName;
        options.RefreshDiscoveryOnSignatureFailure = true;
        var cacheKey = DiscoveryCacheKey(provider, config);
        bool cacheHit = TryApplyCachedDiscovery(options, cacheKey);
        return (new OidcClient(options), options, cacheKey, cacheHit);
    }

    /// <summary>
    /// Exchanges a deferred OpenID callback (see <see cref="OidPost"/>) with the provider,
    /// populating the supplied state with the resulting claims, roles, and access decisions.
    /// </summary>
    /// <param name="provider">The provider being authenticated against.</param>
    /// <param name="config">The provider configuration.</param>
    /// <param name="timedState">The pending login state, populated in place.</param>
    /// <returns>Null on success; otherwise a human-readable error message.</returns>
    private async Task<string> ProcessOidResponse(string provider, OidConfig config, TimedAuthorizeState timedState)
    {
        if (string.IsNullOrEmpty(timedState.CallbackQuery))
        {
            return "Missing or expired login state";
        }

        if (config.Enabled)
        {
            var (oidcClient, options, cacheKey, cacheHit) = BuildOidcClient(provider, config, timedState.RedirectUri);
            var currentState = timedState.State;
            var result = await oidcClient.ProcessResponseAsync(timedState.CallbackQuery, currentState).ConfigureAwait(false);
            if (!cacheHit)
            {
                StoreDiscovery(options, cacheKey);
            }

            if (result.IsError)
            {
                return $"Error logging in: {result.Error} - {result.ErrorDescription}";
            }

            if (!config.EnableFolderRoles && config.EnabledFolders != null)
            {
                timedState.Folders = new List<string>(config.EnabledFolders);
            }
            else
            {
                timedState.Folders = new List<string>();
            }

            timedState.EnableLiveTv = config.EnableLiveTv;
            timedState.EnableLiveTvManagement = config.EnableLiveTvManagement;

            if (config.AvatarUrlFormat is not null)
            {
                timedState.AvatarURL = result.User.Claims.Aggregate(
                    config.AvatarUrlFormat,
                    (s, claim) => s.Contains($"@{{{claim.Type}}}") ? s.Replace($"@{{{claim.Type}}}", claim.Value) : s);
            }

            foreach (var claim in result.User.Claims)
            {
                // Capture the immutable subject identifier; it is the canonical link key (the
                // username claim is mutable and must not be used to identify the account).
                if (claim.Type == "sub")
                {
                    timedState.SubjectId = claim.Value;
                }

                if (claim.Type == (config.DefaultUsernameClaim?.Trim() ?? "preferred_username"))
                {
                    timedState.Username = claim.Value;
                    if (config.Roles == null || config.Roles.Length == 0)
                    {
                        timedState.Valid = true;
                    }
                }

                // Role processing
                // The regex matches any "." not preceded by a "\": a.b.c will be split into a, b, and c, but a.b\.c will be split into a, b.c (after processing the escaped dots)
                // We have to first process the RoleClaim string
                string[] segments = string.IsNullOrEmpty(config.RoleClaim) ? Array.Empty<string>() : Regex.Split(config.RoleClaim.Trim(), "(?<!\\\\)\\.");

                if (segments.Any())
                {
                    // Now we make sure that any escaped "."s ("\.") are replaced with "."
                    segments = segments.Select(i => i.Replace("\\.", ".")).ToArray();

                    if (claim.Type == segments[0])
                    {
                        List<string> roles;
                        // If we are not using JSON values, just use the raw info from the claim value
                        if (segments.Length == 1)
                        {
                            roles = new List<string> { claim.Value };
                        }
                        else
                        {
                            // We recursively traverse through the JSON data for the roles and parse it
                            var json = JsonConvert.DeserializeObject<IDictionary<string, object>>(claim.Value);
                            if (json is null)
                            {
                                roles = new List<string>();
                            }
                            else
                            {
                                bool missingSegment = false;
                                for (int i = 1; i < segments.Length - 1; i++)
                                {
                                    var segment = segments[i];
                                    if (!json.TryGetValue(segment, out var nextToken) || nextToken is not JObject nextObject)
                                    {
                                        missingSegment = true;
                                        break;
                                    }

                                    json = nextObject.ToObject<IDictionary<string, object>>();
                                    if (json is null)
                                    {
                                        missingSegment = true;
                                        break;
                                    }
                                }

                                if (missingSegment || !json.TryGetValue(segments[^1], out var rolesToken) || rolesToken is not JArray rolesArray)
                                {
                                    roles = new List<string>();
                                }
                                else
                                {
                                    // The final step is to take the JSON and turn it from a dictionary into a string
                                    roles = rolesArray.ToObject<List<string>>();
                                }
                            }
                        }

                        foreach (string role in roles)
                        {
                            // Check if allowed to login based on roles
                            if (config.Roles != null && config.Roles.Any())
                            {
                                foreach (string validRoles in config.Roles)
                                {
                                    if (role.Equals(validRoles))
                                    {
                                        timedState.Valid = true;
                                    }
                                }
                            }

                            // Check if admin based on roles
                            if (config.AdminRoles != null && config.AdminRoles.Any())
                            {
                                foreach (string validAdminRoles in config.AdminRoles)
                                {
                                    if (role.Equals(validAdminRoles))
                                    {
                                        timedState.Admin = true;
                                    }
                                }
                            }

                            // Get allowed folders from roles
                            if (config.EnableFolderRoles)
                            {
                                foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping)
                                {
                                    if (role.Equals(folderRoleMap.Role?.Trim()))
                                    {
                                        timedState.Folders.AddRange(folderRoleMap.Folders);
                                    }
                                }
                            }

                            if (config.EnableLiveTvRoles)
                            {
                                // Check if allowed Live TV based on roles
                                if (config.LiveTvRoles != null && config.LiveTvRoles.Any())
                                {
                                    foreach (string validLiveTvRoles in config.LiveTvRoles)
                                    {
                                        if (role.Equals(validLiveTvRoles))
                                        {
                                            timedState.EnableLiveTv = true;
                                        }
                                    }
                                }

                                // Check if allowed Live TV management based on roles
                                if (config.LiveTvManagementRoles != null && config.LiveTvManagementRoles.Any())
                                {
                                    foreach (string validLiveTvManagementRoles in config.LiveTvManagementRoles)
                                    {
                                        if (role.Equals(validLiveTvManagementRoles))
                                        {
                                            timedState.EnableLiveTvManagement = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // If the provider doesn't support the preferred username claim, then use the sub claim
            if (!timedState.Valid)
            {
                foreach (var claim in result.User.Claims)
                {
                    if (claim.Type == "sub")
                    {
                        timedState.Username = claim.Value;
                        if (config.Roles == null || config.Roles.Length == 0)
                        {
                            timedState.Valid = true;
                        }
                    }
                }
            }

            // Guarantee a canonical link key even for providers that omit "sub".
            timedState.SubjectId ??= timedState.Username;

            if (timedState.Valid)
            {
                return null;
            }

            // Log only claim *types*, never their values: claim values can contain emails, group
            // lists, or tokens that should not be written to (often shared) server logs.
            _logger.LogWarning(
                "OpenID user {Username} is missing a required role. Claim types present: {@ClaimTypes}. Expected any one of: {@ExpectedRoles}",
                timedState.Username,
                result.User.Claims.Select(o => o.Type).Distinct(),
                config.Roles);

            return "Error. Check permissions.";
        }

        // If the config doesn't have an active provider matching the request, show an error
        return "No matching provider found";
    }

    /// <summary>
    /// Ensures the deferred OpenID callback for the given state is exchanged with the
    /// provider exactly once, caching the outcome so the linking and auth POSTs from
    /// the loading page don't replay the single-use authorization code.
    /// </summary>
    /// <param name="provider">The provider being authenticated against.</param>
    /// <param name="config">The provider configuration.</param>
    /// <param name="timedState">The pending login state.</param>
    /// <returns>Null on success; otherwise a human-readable error message.</returns>
    private async Task<string> EnsureOidStateProcessed(string provider, OidConfig config, TimedAuthorizeState timedState)
    {
        if (timedState.Processed)
        {
            return timedState.ProcessError;
        }

        await timedState.ProcessLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!timedState.Processed)
            {
                timedState.ProcessError = await ProcessOidResponse(provider, config, timedState).ConfigureAwait(false);
                timedState.Processed = true;
            }
        }
        finally
        {
            timedState.ProcessLock.Release();
        }

        return timedState.ProcessError;
    }

    /// <summary>
    /// Initiates the login flow for OpenID. This redirects the user to the auth provider.
    /// </summary>
    /// <param name="provider">The name of the provider.</param>
    /// <param name="isLinking">Whether or not this request is to link accounts (Rather than authenticate).</param>
    /// <returns>An asynchronous result for the authentication.</returns>
    [HttpGet("OID/p/{provider}")]
    [HttpGet("OID/start/{provider}")]
    public async Task<ActionResult> OidChallenge(string provider, [FromQuery] bool isLinking = false)
    {
        Invalidate();
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException("Provider does not exist");
        }

        if (config.Enabled)
        {
            bool newPath = config.NewPath;
            if (!isLinking)
            {
                newPath = Request.Path.Value.Contains("/start/", StringComparison.InvariantCultureIgnoreCase);
                config.NewPath = newPath;
            }

            string redirectUri = GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/OID/{(newPath ? "redirect" : "r")}/" + provider;

            var (oidcClient, options, cacheKey, cacheHit) = BuildOidcClient(provider, config, redirectUri);
            var state = await oidcClient.PrepareLoginAsync().ConfigureAwait(false);
            if (!cacheHit)
            {
                StoreDiscovery(options, cacheKey);
            }

            if (state.IsError)
            {
                return ReturnError(StatusCodes.Status400BadRequest, $"Error preparing login: {state.Error} - {state.ErrorDescription}");
            }

            StateManager[state.State] = new TimedAuthorizeState(state, DateTime.UtcNow);

            // Track whether this is a linking request or not.
            StateManager[state.State].IsLinking = isLinking;

            // Persist the redirect URI so the deferred token exchange (run from the
            // /OID/Auth or /Link POST) replays the exact value sent to the provider.
            StateManager[state.State].RedirectUri = redirectUri;
            return Redirect(state.StartUrl);
        }

        throw new ArgumentException("Provider does not exist");
    }

    /// <summary>
    /// Adds an OpenID auth configuration. Requires administrator privileges. If the provider already exists, it will be removed and readded.
    /// </summary>
    /// <param name="provider">The name of the provider to add.</param>
    /// <param name="config">The OID configuration (deserialized from a JSON post).</param>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("OID/Add/{provider}")]
    public void OidAdd(string provider, [FromBody] OidConfig config)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.OidConfigs[provider] = config;
        SSOPlugin.Instance.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Deletes an OpenID provider.
    /// </summary>
    /// <param name="provider">Name of provider to delete.</param>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/Del/{provider}")]
    public void OidDel(string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.OidConfigs.Remove(provider);
        SSOPlugin.Instance.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Lists the OpenID providers configured. Requires administrator privileges.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("OID/Get")]
    public ActionResult OidProviders()
    {
        return Ok(SSOPlugin.Instance.Configuration.OidConfigs);
    }

    /// <summary>
    /// Lists the OpenID providers names only.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [HttpGet("OID/GetNames")]
    public ActionResult OidProviderNames()
    {
        return Ok(SSOPlugin.Instance.Configuration.OidConfigs.Keys);
    }

    /// <summary>
    /// Lists the SAML providers names only.
    /// </summary>
    /// <returns>The list of OpenID configurations.</returns>
    [HttpGet("SAML/GetNames")]
    public ActionResult SamlProviderNames()
    {
        return Ok(SSOPlugin.Instance.Configuration.SamlConfigs.Keys);
    }

    /// <summary>
    /// This endpoint accepts JSON and will authorize the user from the device values passed from the client.
    /// </summary>
    /// <param name="provider">Name of provider to authenticate against.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [HttpPost("OID/Auth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> OidAuth(string provider, [FromBody] AuthResponse response)
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            Invalidate();
            foreach (var kvp in StateManager)
            {
                if (kvp.Value.State.State.Equals(response.Data))
                {
                    // Perform the deferred provider token exchange now (see OidPost), so it
                    // runs while the loading page is already on screen rather than before it.
                    var error = await EnsureOidStateProcessed(provider, config, kvp.Value).ConfigureAwait(false);
                    if (error != null)
                    {
                        StateManager.TryRemove(kvp.Key, out _);
                        return Problem(error);
                    }

                    if (!kvp.Value.Valid)
                    {
                        continue;
                    }

                    Guid? userId = await CreateCanonicalLinkAndUserIfNotExist("oid", provider, kvp.Value.SubjectId, kvp.Value.Username, config.EnableUnverifiedLinking);
                    if (userId is null)
                    {
                        StateManager.TryRemove(kvp.Key, out _);
                        return Problem(UnverifiedLinkingError);
                    }

                    var authenticationResult = await Authenticate(
                        userId.Value,
                        kvp.Value.Admin,
                        config.EnableAuthorization,
                        config.EnableAllFolders,
                        kvp.Value.Folders.ToArray(),
                        kvp.Value.EnableLiveTv,
                        kvp.Value.EnableLiveTvManagement,
                        response,
                        config.DefaultProvider?.Trim(),
                        kvp.Value.AvatarURL,
                        config.AllowAvatarLocalNetwork)
                        .ConfigureAwait(false);
                    StateManager.TryRemove(kvp.Key, out _);
                    return Ok(authenticationResult);
                }
            }
        }

        return Problem("Something went wrong");
    }

    /// <summary>
    /// This is the callback for the SAML flow. This creates a webpage to complete auth.
    /// </summary>
    /// <param name="provider">The provider that is calling back.</param>
    /// <param name="relayState">
    ///    RelayState given in the original saml request. If it is equal to "linking",
    ///    We consider this to be a linking request.
    /// </param>
    /// <returns>A webpage that will complete the client-side flow.</returns>
    [HttpPost("SAML/p/{provider}")]
    [HttpPost("SAML/post/{provider}")]
    public ActionResult SamlPost(string provider, [FromQuery] string relayState = null)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        bool isLinking = relayState == "linking";

        _logger.LogInformation(
            $"SAML request has relayState of {relayState}");

        if (config.Enabled)
        {
            var samlResponse = new Response(config.SamlCertificate, Request.Form["SAMLResponse"]);

            if (!samlResponse.IsValid())
            {
                return Problem("Invalid SAML signature");
            }

            if (!AudienceAllowed(config, samlResponse))
            {
                return Problem("SAML assertion audience does not match this service provider.");
            }

            bool valid = false;

            // If no roles are configured, don't use RBAC
            if (config.Roles == null || config.Roles.Length == 0)
            {
                valid = true;
            }

            // Check if user is allowed to log in based on roles
            if (config.Roles != null)
            {
                foreach (string role in samlResponse.GetCustomAttributes("Role"))
                {
                    foreach (string allowedRole in config.Roles)
                    {
                        if (allowedRole.Equals(role))
                        {
                            valid = true;
                        }
                    }
                }
            }

            if (valid)
            {
                return Content(
                        WebResponse.Generator(
                            data: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(samlResponse.Xml)),
                            provider: provider,
                            baseUrl: GetRequestBase(config.SchemeOverride, config.PortOverride),
                            mode: "SAML",
                            isLinking: isLinking),
                        MediaTypeNames.Text.Html);
            }

            _logger.LogWarning(
                "SAML user: {UserId} has insufficient roles: {@Roles}. Expected any one of: {@ExpectedRoles}",
                samlResponse.GetNameID(),
                samlResponse.GetCustomAttributes("Role"),
                config.Roles);
            return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
        }

        return ReturnError(StatusCodes.Status400BadRequest, "No active providers found");
    }

    /// <summary>
    /// Initializes the SAML flow. This will redirect the user to the SAML provider.
    /// </summary>
    /// <param name="provider">The provider to being the flow with.</param>
    /// <param name="isLinking">Whether this flow intends to link an account, or initiate auth.</param>
    /// <returns>A redirect to the SAML provider's auth page.</returns>
    [HttpGet("SAML/p/{provider}")]
    [HttpGet("SAML/start/{provider}")]
    public RedirectResult SamlChallenge(string provider, [FromQuery] bool isLinking = false)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            throw new ArgumentException("Provider does not exist");
        }

        if (config.Enabled)
        {
            bool newPath = config.NewPath;
            if (!isLinking)
            {
                newPath = Request.Path.Value.Contains("/start/", StringComparison.InvariantCultureIgnoreCase);
                config.NewPath = newPath;
            }

            string redirectUri = GetRequestBase(config.SchemeOverride, config.PortOverride) + $"/sso/SAML/{(newPath ? "post" : "p")}/" + provider;
            string relayState = null;
            if (isLinking)
            {
                relayState = "linking";
            }

            var request = new AuthRequest(
                config.SamlClientId.Trim(),
                redirectUri);

            return Redirect(request.GetRedirectUrl(config.SamlEndpoint.Trim(), relayState));
        }

        throw new ArgumentException("Provider does not exist");
    }

    /// <summary>
    /// Adds a SAML configuration. If the provider already exists, overwrite it.
    /// </summary>
    /// <param name="provider">The provider name to add.</param>
    /// <param name="newConfig">The SAML configuration object (deserialized) from JSON.</param>
    /// <returns>The success result.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("SAML/Add/{provider}")]
    public OkResult SamlAdd(string provider, [FromBody] SamlConfig newConfig)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.SamlConfigs[provider] = newConfig;
        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Deletes a provider from the configuration with a given ID.
    /// </summary>
    /// <param name="provider">The ID of the provider to delete.</param>
    /// <returns>The success result.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("SAML/Del/{provider}")]
    public OkResult SamlDel(string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        configuration.SamlConfigs.Remove(provider);
        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Returns a list of all SAML providers configured. Requires administrator privileges.
    /// </summary>
    /// <returns>A list of all of the Saml providers available.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpGet("SAML/Get")]
    public ActionResult SamlProviders()
    {
        return Ok(SSOPlugin.Instance.Configuration.SamlConfigs);
    }

    /// <summary>
    /// This endpoint accepts JSON and will authorize the user from the device values passed from the client.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [HttpPost("SAML/Auth/{provider}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> SamlAuth(string provider, [FromBody] AuthResponse response)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        if (config.Enabled)
        {
            bool isAdmin = false;
            bool liveTv = config.EnableLiveTv;
            bool liveTvManagement = config.EnableLiveTvManagement;
            var samlResponse = new Response(config.SamlCertificate, response.Data);

            if (!samlResponse.IsValid())
            {
                return Problem("Invalid SAML signature");
            }

            if (!AudienceAllowed(config, samlResponse))
            {
                return Problem("SAML assertion audience does not match this service provider.");
            }

            // /SAML/Auth is directly callable, so it must enforce the login role allow-list itself
            // rather than relying on the /SAML/post check that precedes it in the browser flow.
            var assertionRoles = samlResponse.GetCustomAttributes("Role");
            if (config.Roles != null && config.Roles.Length > 0
                && !assertionRoles.Any(role => config.Roles.Contains(role)))
            {
                _logger.LogWarning(
                    "SAML user: {UserId} has insufficient roles: {@Roles}. Expected any one of: {@ExpectedRoles}",
                    samlResponse.GetNameID(),
                    assertionRoles,
                    config.Roles);
                return ReturnError(StatusCodes.Status401Unauthorized, "Error. Check permissions.");
            }

            // Reject replays of an assertion that has already been consumed by a previous login.
            if (!TryConsumeSamlAssertion(provider, samlResponse))
            {
                return Problem("This SAML assertion has already been used. Please sign in again.");
            }

            List<string> folders;
            if (!config.EnableFolderRoles && config.EnabledFolders != null)
            {
                folders = new List<string>(config.EnabledFolders);
            }
            else
            {
                folders = new List<string>();
            }

            foreach (string role in samlResponse.GetCustomAttributes("Role"))
            {
                if (config.AdminRoles != null)
                {
                    foreach (string allowedRole in config.AdminRoles)
                    {
                        if (allowedRole.Equals(role))
                        {
                            isAdmin = true;
                        }
                    }
                }

                if (config.EnableFolderRoles)
                {
                    if (config.FolderRoleMapping != null)
                    {
                        foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping)
                        {
                            if (folderRoleMap.Role.Equals(role))
                            {
                                folders.AddRange(folderRoleMap.Folders);
                            }
                        }
                    }
                }

                if (config.EnableLiveTvRoles)
                {
                    if (config.LiveTvRoles != null)
                    {
                        foreach (string allowedLiveTvRole in config.LiveTvRoles)
                        {
                            if (allowedLiveTvRole.Equals(role))
                            {
                                liveTv = true;
                            }
                        }
                    }

                    if (config.LiveTvManagementRoles != null)
                    {
                        foreach (string allowedLiveTvManagementRole in config.LiveTvManagementRoles)
                        {
                            if (allowedLiveTvManagementRole.Equals(role))
                            {
                                liveTvManagement = true;
                            }
                        }
                    }
                }
            }

            string nameId = samlResponse.GetNameID();
            if (string.IsNullOrEmpty(nameId))
            {
                return Problem("SAML assertion is missing a NameID");
            }

            Guid? userId = await CreateCanonicalLinkAndUserIfNotExist("saml", provider, nameId, nameId, config.EnableUnverifiedLinking);
            if (userId is null)
            {
                return Problem(UnverifiedLinkingError);
            }

            var authenticationResult = await Authenticate(
                userId.Value,
                isAdmin,
                config.EnableAuthorization,
                config.EnableAllFolders,
                folders.ToArray(),
                liveTv,
                liveTvManagement,
                response,
                config.DefaultProvider?.Trim(),
                null)
                .ConfigureAwait(false);
            return Ok(authenticationResult);
        }

        return Problem("Something went wrong");
    }

    /// <summary>
    /// Removes a user from SSO auth and switches it back to another auth provider. Requires administrator privileges.
    /// </summary>
    /// <param name="username">The username to switch to the new provider.</param>
    /// <param name="provider">The new provider to switch to.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize(Policy = Policies.RequiresElevation)]
    [HttpPost("Unregister/{username}")]
    public async Task<ActionResult> Unregister(string username, [FromBody] string provider)
    {
        User user = _userManager.GetUserByName(username);
        if (user == null)
        {
            return NotFound("No user found with that username");
        }

        user.AuthenticationProviderId = provider;
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        return Ok();
    }

    private SerializableDictionary<string, Guid> GetCanonicalLinks(string mode, string provider)
    {
        SerializableDictionary<string, Guid> links = null;

        switch (mode.ToLower())
        {
            case "saml":
                links = SSOPlugin.Instance.Configuration.SamlConfigs[provider].CanonicalLinks;
                break;
            case "oid":
                links = SSOPlugin.Instance.Configuration.OidConfigs[provider].CanonicalLinks;
                break;
            default:
                throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'");
        }

        if (links == null)
        {
            links = new SerializableDictionary<string, Guid>();
        }

        return links;
    }

    /// <summary>
    /// Resolves the Jellyfin user for an authenticated provider identity, creating the user and/or
    /// canonical link as needed. Identity is keyed on the immutable <paramref name="subjectId"/>
    /// (OIDC <c>sub</c> / SAML NameID), never on the mutable username, so a renamed provider identity
    /// cannot collide with another account.
    /// </summary>
    /// <param name="mode">The provider mode; "saml" or "oid".</param>
    /// <param name="provider">The provider name.</param>
    /// <param name="subjectId">The immutable subject identifier used as the canonical link key.</param>
    /// <param name="username">The display username used when provisioning a new account.</param>
    /// <param name="enableUnverifiedLinking">
    ///   Whether a first-time login may bind to a pre-existing account with the same username.
    /// </param>
    /// <returns>
    ///   The Jellyfin user id, or <c>null</c> if a same-named account exists that this login is not
    ///   permitted to take over.
    /// </returns>
    private async Task<Guid?> CreateCanonicalLinkAndUserIfNotExist(string mode, string provider, string subjectId, string username, bool enableUnverifiedLinking)
    {
        if (string.IsNullOrEmpty(subjectId))
        {
            subjectId = username;
        }

        // 1. The trusted path: an existing canonical link keyed on the immutable subject identifier.
        var linkedId = TryGetCanonicalLink(mode, provider, subjectId);
        if (linkedId != Guid.Empty)
        {
            var linkedUser = _userManager.GetUserById(linkedId);
            if (linkedUser != null)
            {
                return linkedUser.Id;
            }
        }

        // 2. Migrate a legacy link keyed on the (mutable) username written by older plugin versions,
        //    re-keying it to the subject identifier so subsequent logins use the stable key.
        if (!string.Equals(subjectId, username, StringComparison.Ordinal))
        {
            var legacyId = TryGetCanonicalLink(mode, provider, username);
            if (legacyId != Guid.Empty)
            {
                var legacyUser = _userManager.GetUserById(legacyId);
                if (legacyUser != null)
                {
                    MutateCanonicalLinks(mode, provider, links =>
                    {
                        links.Remove(username);
                        links[subjectId] = legacyUser.Id;
                    });
                    _logger.LogInformation("Migrated legacy SSO link for {Username} to its subject identifier.", username);
                    return legacyUser.Id;
                }
            }
        }

        // 3. No link exists. A Jellyfin account may already exist with this username, but silently
        //    claiming it is the account-takeover vector. Existing accounts are attached to an SSO
        //    identity only through the authenticated self-service linking page, unless the operator
        //    has explicitly opted in to unverified linking.
        var existingByName = _userManager.GetUserByName(username);
        if (existingByName != null)
        {
            if (!enableUnverifiedLinking)
            {
                _logger.LogWarning(
                    "Refusing SSO login for subject {Subject}: a Jellyfin account named {Username} already exists without a link to this provider. Use self-service linking or enable unverified linking.",
                    subjectId,
                    username);
                return null;
            }

            CreateCanonicalLink(mode, provider, existingByName.Id, subjectId);
            return existingByName.Id;
        }

        // 4. Provision a fresh, plugin-managed account with a random password so it cannot be
        //    logged into directly.
        _logger.LogInformation("SSO user {Username} doesn't exist, creating...", username);
        var user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
        user.AuthenticationProviderId = GetType().FullName;
        // https://jonathancrozier.com/blog/how-to-generate-a-cryptographically-secure-random-string-in-dot-net-with-c-sharp
        user.Password = _cryptoProvider.CreatePasswordHash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))).ToString();
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        CreateCanonicalLink(mode, provider, user.Id, subjectId);
        return user.Id;
    }

    private Guid GetCanonicalLink(string mode, string provider, string canonicalName)
    {
        SerializableDictionary<string, Guid> links = null;
        Guid userId = Guid.Empty;

        links = GetCanonicalLinks(mode, provider);

        userId = links[canonicalName];

        return userId;
    }

    // Returns the linked user id for a canonical name, or Guid.Empty if there is no such link.
    private Guid TryGetCanonicalLink(string mode, string provider, string canonicalName)
    {
        try
        {
            return GetCanonicalLink(mode, provider, canonicalName);
        }
        catch (KeyNotFoundException)
        {
            return Guid.Empty;
        }
    }

    /// <summary>
    /// Create a canonical link for a given user. Must be performed by the user being changed, or admin.
    /// </summary>
    /// <param name="mode">The mode of the function; SAML or OID.</param>
    /// <param name="provider">The name of the provider to link to a jellyfin account.</param>
    /// <param name="jellyfinUserId">The user ID within jellyfin to link to the provider.</param>
    /// <param name="authResponse">The client information to authenticate the user with.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize]
    [HttpPost("{mode}/Link/{provider}/{jellyfinUserId}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> AddCanonicalLink([FromRoute] string mode, [FromRoute] string provider, [FromRoute] Guid jellyfinUserId, [FromBody] AuthResponse authResponse)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "User is not allowed to link SSO providers.");
        }

        switch (mode.ToLower())
        {
            case "saml":
                return SamlLink(provider, jellyfinUserId, authResponse);
            case "oid":
                return await OidLink(provider, jellyfinUserId, authResponse);
            default:
                throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'");
        }
    }

    /// <summary>
    /// Unregisters a given mapping from id within provider to user.
    /// </summary>
    /// <param name="mode">The mode of the function; SAML or OID.</param>
    /// <param name="provider">The name of the provider from which the link should be removed.</param>
    /// <param name="jellyfinUserId">The user ID within jellyfin to unlink from the provider.</param>
    /// <param name="canonicalName">The user ID within jellyfin to unlink.</param>
    /// <returns>Whether this API endpoint succeeded.</returns>
    [Authorize]
    [HttpDelete("{mode}/Link/{provider}/{jellyfinUserId}/{canonicalName}")]
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult> DeleteCanonicalLink([FromRoute] string mode, [FromRoute] string provider, [FromRoute] Guid jellyfinUserId, [FromRoute] string canonicalName)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Current user is not allowed to unlink SSO providers for user ID.");
        }

        Guid linkedId = GetCanonicalLink(mode, provider, canonicalName);

        if (linkedId != jellyfinUserId)
        {
            return StatusCode(StatusCodes.Status409Conflict, "jellyfin UID does not match id registered to that canonical name.");
        }

        MutateCanonicalLinks(mode, provider, links => links.Remove(canonicalName));

        return Ok();
    }

    /// <summary>
    /// Gets all the saml links for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The user ID within jellyfin for which to return the links.</param>
    /// <returns>A dictionary of provider : link mappings.</returns>
    [Authorize]
    [HttpGet("saml/links/{jellyfinUserId}")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<SerializableDictionary<string, IEnumerable<string>>>> GetSamlLinksByUser(Guid jellyfinUserId)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Non-admin is not allowed to query other user's mappings.");
        }

        var mappings = new SerializableDictionary<string, IEnumerable<string>>();
        var providerList = SSOPlugin.Instance.Configuration.SamlConfigs;

        foreach (var providerName in providerList.Keys)
        {
            var canonLinks = providerList[providerName].CanonicalLinks;
            var canonKeys = from link in canonLinks where link.Value == jellyfinUserId select link.Key;
            mappings[providerName] = canonKeys;
        }

        return mappings;
    }

    /// <summary>
    /// Gets all the oid links for a user.
    /// </summary>
    /// <param name="jellyfinUserId">The user ID within jellyfin for which to return the links.</param>
    /// <returns>A dictionary of provider : link mappings.</returns>
    [Authorize]
    [HttpGet("oid/links/{jellyfinUserId}")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<SerializableDictionary<string, IEnumerable<string>>>> GetOidLinksByUser(Guid jellyfinUserId)
    {
        if (!await RequestHelpers.AssertCanUpdateUser(_authContext, HttpContext.Request, jellyfinUserId, true).ConfigureAwait(false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Non-admin is not allowed to query other user's mappings.");
        }

        var mappings = new SerializableDictionary<string, IEnumerable<string>>();
        var providerList = SSOPlugin.Instance.Configuration.OidConfigs;

        foreach (var providerName in providerList.Keys)
        {
            var canonLinks = providerList[providerName].CanonicalLinks;
            var canonKeys = from link in canonLinks where link.Value == jellyfinUserId select link.Key;
            mappings[providerName] = canonKeys;
        }

        return mappings;
    }

    /// <summary>
    /// Validate a saml link request and create the link if it is valid.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="jellyfinUserId">
    ///   The ID of the account to be linked to the provider.
    ///   Must be performed by this user, or an admin.
    /// </param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    private ActionResult SamlLink(string provider, Guid jellyfinUserId, AuthResponse response)
    {
        SamlConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.SamlConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        var samlResponse = new Response(config.SamlCertificate, response.Data);

        if (!samlResponse.IsValid())
        {
            return Problem("Invalid SAML signature");
        }

        if (!AudienceAllowed(config, samlResponse))
        {
            return Problem("SAML assertion audience does not match this service provider.");
        }

        string providerUserId = samlResponse.GetNameID();
        if (string.IsNullOrEmpty(providerUserId))
        {
            return Problem("SAML assertion is missing a NameID");
        }

        return CreateCanonicalLink("saml", provider, jellyfinUserId, providerUserId);
    }

    /// <summary>
    /// Validate an OIDC link request and create the link if it is valid.
    /// </summary>
    /// <param name="provider">The provider to authenticate against.</param>
    /// <param name="jellyfinUserId">
    ///   The ID of the account to be linked to the provider.
    ///   Must be performed by this user, or an admin.
    /// </param>
    /// <param name="response">The data passed to the client to ensure it is the right one.</param>
    /// <returns>JSON for the client to populate information with.</returns>
    [Consumes(MediaTypeNames.Application.Json)]
    [Produces(MediaTypeNames.Application.Json)]
    private async Task<ActionResult> OidLink(string provider, Guid jellyfinUserId, AuthResponse response)
    {
        OidConfig config;
        try
        {
            config = SSOPlugin.Instance.Configuration.OidConfigs[provider];
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        Invalidate();
        foreach (var kvp in StateManager)
        {
            if (kvp.Value.State.State.Equals(response.Data))
            {
                // The linking POST arrives before the auth POST, so run the deferred
                // provider token exchange here if it hasn't happened yet (see OidPost).
                var error = await EnsureOidStateProcessed(provider, config, kvp.Value).ConfigureAwait(false);
                if (error != null)
                {
                    return Problem(error);
                }

                if (kvp.Value.Valid)
                {
                    string providerUserId = kvp.Value.SubjectId;
                    return CreateCanonicalLink("oid", provider, jellyfinUserId, providerUserId);
                }
            }
        }

        return Problem("Something went wrong!");
    }

    private ActionResult CreateCanonicalLink(string mode, string provider, [FromRoute] Guid jellyfinUserId, string providerUserId)
    {
        try
        {
            MutateCanonicalLinks(mode, provider, links => links[providerUserId] = jellyfinUserId);
        }
        catch (KeyNotFoundException)
        {
            return BadRequest("No matching provider found");
        }

        return NoContent();
    }

    // Applies a mutation to a provider's canonical links under a lock, using copy-on-write: the
    // mutation runs on a fresh copy that is then swapped in, so concurrent readers always see a
    // complete snapshot and concurrent writers are serialized.
    private void MutateCanonicalLinks(string mode, string provider, Action<SerializableDictionary<string, Guid>> mutate)
    {
        lock (CanonicalLinkLock)
        {
            var links = new SerializableDictionary<string, Guid>(GetCanonicalLinks(mode, provider));
            mutate(links);
            UpdateCanonicalLinkConfig(links, mode, provider);
        }
    }

    private OkResult UpdateCanonicalLinkConfig(SerializableDictionary<string, Guid> links, string mode, string provider)
    {
        var configuration = SSOPlugin.Instance.Configuration;
        switch (mode.ToLower())
        {
            case "saml":
                configuration.SamlConfigs[provider].CanonicalLinks = links;
                break;
            case "oid":
                configuration.OidConfigs[provider].CanonicalLinks = links;
                break;
            default:
                throw new ArgumentException($"{mode} is not a valid choice between 'saml' and 'oid'");
        }

        SSOPlugin.Instance.UpdateConfiguration(configuration);
        return Ok();
    }

    /// <summary>
    /// Authenticates the user with the given information.
    /// </summary>
    /// <param name="userId">The user id of the user to authenticate.</param>
    /// <param name="isAdmin">Determines whether this user is an administrator.</param>
    /// <param name="enableAuthorization">Determines whether RBAC is used for this user.</param>
    /// <param name="enableAllFolders">Determines whether all folders are enabled.</param>
    /// <param name="enabledFolders">Determines which folders should be enabled for this client.</param>
    /// <param name="enableLiveTv">Determines whether live TV access is allowed for this user.</param>
    /// <param name="enableLiveTvAdmin">Determines whether live TV can be managed by this user.</param>
    /// <param name="authResponse">The client information to authenticate the user with.</param>
    /// <param name="defaultProvider">The default provider of the user to be set after logging in.</param>
    /// <param name="avatarUrl">The new avatar url for the user.</param>
    /// <param name="allowAvatarLocalNetwork">Whether the avatar fetch may target private/loopback/link-local addresses.</param>
    private async Task<AuthenticationResult> Authenticate(Guid userId, bool isAdmin, bool enableAuthorization, bool enableAllFolders, string[] enabledFolders, bool enableLiveTv, bool enableLiveTvAdmin, AuthResponse authResponse, string defaultProvider, string avatarUrl, bool allowAvatarLocalNetwork = false)
    {
        User user = _userManager.GetUserById(userId);
        if (enableAuthorization)
        {
            user.SetPermission(PermissionKind.IsAdministrator, isAdmin);
            user.SetPermission(PermissionKind.EnableAllFolders, enableAllFolders);
            if (!enableAllFolders)
            {
                user.SetPreference(PreferenceKind.EnabledFolders, enabledFolders);
            }
        }

        if (avatarUrl is not null)
        {
            try
            {
                if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var avatarUri)
                    || (avatarUri.Scheme != Uri.UriSchemeHttp && avatarUri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new InvalidOperationException("Avatar URL must be an absolute http(s) URL: " + avatarUrl);
                }

                // Dedicated client whose connections are validated against SSRF (see CreateAvatarHttpClient).
                using var client = CreateAvatarHttpClient(allowAvatarLocalNetwork);
                AddPluginUserAgent(client);

                var avatarResponse = await client.GetAsync(avatarUri);

                if (!avatarResponse.Content.Headers.TryGetValues("content-type", out var contentTypeList))
                {
                    throw new Exception("Cannot get Content-Type of image : " + avatarUrl);
                }

                // Allow-list known raster image types and map to a safe extension. This rejects
                // image/svg+xml (an SVG profile image can carry script) and fixes the missing-dot
                // filename ("profile" + extension).
                var mediaType = contentTypeList.First().Split(';')[0].Trim().ToLowerInvariant();
                var extension = mediaType switch
                {
                    "image/png" => ".png",
                    "image/jpeg" => ".jpg",
                    "image/gif" => ".gif",
                    "image/webp" => ".webp",
                    _ => null
                };
                if (extension is null)
                {
                    throw new InvalidOperationException("Unsupported avatar content type: " + mediaType);
                }

                var stream = await avatarResponse.Content.ReadAsStreamAsync();

                if (user != null)
                {
                    var userDataPath =
                        Path.Combine(
                            _serverConfigurationManager.ApplicationPaths.UserConfigurationDirectoryPath,
                            user.Username);
                    if (user.ProfileImage is not null)
                    {
                        await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
                    }

                    user.ProfileImage = new ImageInfo(Path.Combine(userDataPath, "profile" + extension));

                    await _providerManager.SaveImage(stream, mediaType, user.ProfileImage.Path)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e.Message);
            }
        }

        user.SetPermission(PermissionKind.EnableLiveTvAccess, enableLiveTv);
        user.SetPermission(PermissionKind.EnableLiveTvManagement, enableLiveTvAdmin);

        // Apply the default provider before saving so the user is persisted only once per login.
        if (!string.IsNullOrEmpty(defaultProvider))
        {
            user.AuthenticationProviderId = defaultProvider;
            _logger.LogInformation("Set default login provider to " + defaultProvider);
        }

        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        var authRequest = new AuthenticationRequest();
        authRequest.UserId = user.Id;
        authRequest.Username = user.Username;
        authRequest.App = authResponse.AppName;
        authRequest.AppVersion = authResponse.AppVersion;
        authRequest.DeviceId = authResponse.DeviceID;
        authRequest.DeviceName = authResponse.DeviceName;
        _logger.LogInformation("Auth request created...");

        return await _sessionManager.AuthenticateDirect(authRequest).ConfigureAwait(false);
    }

    // Builds an HttpClient for fetching avatars whose every connection (including redirect hops) is
    // validated at connect time, so a malicious or user-influenced avatar URL cannot be used for SSRF
    // against loopback/private/link-local addresses (e.g. cloud metadata at 169.254.169.254). Validating
    // the resolved address we actually dial also closes the DNS-rebinding window.
    private static HttpClient CreateAvatarHttpClient(bool allowLocalNetwork)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
                foreach (var address in addresses)
                {
                    if (!allowLocalNetwork && IsDisallowedAddress(address))
                    {
                        continue;
                    }

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }

                throw new InvalidOperationException("Refusing to fetch avatar from a non-public address: " + context.DnsEndPoint.Host);
            }
        };

        return new HttpClient(handler, disposeHandler: true);
    }

    // True for addresses that must not be reachable via an avatar fetch: loopback, private, link-local
    // (incl. the cloud metadata service), CGNAT, unique/site-local IPv6, and unspecified addresses.
    private static bool IsDisallowedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // Blocks 0.0.0.0/8, 10/8, 100.64/10 (CGNAT), 169.254/16 (link-local incl. metadata),
            // 172.16/12, and 192.168/16.
            var b = address.GetAddressBytes();
            return b[0] == 0
                || b[0] == 10
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Blocks link-local (fe80::/10), site-local (fec0::/10), and unique-local (fc00::/7).
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || (address.GetAddressBytes()[0] & 0xFE) == 0xFC
                || address.Equals(IPAddress.IPv6Any);
        }

        return true;
    }

    private void Invalidate()
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in StateManager)
        {
            if (now.Subtract(kvp.Value.Created) > StateTtl)
            {
                StateManager.TryRemove(kvp.Key, out _);
            }
        }
    }

    // Records a signature-valid SAML assertion as consumed, returning false if it has already been
    // used (a replay). Only assertions that have passed signature and validity-window checks reach
    // here, so the cache cannot be poisoned with forged IDs.
    private static bool TryConsumeSamlAssertion(string provider, Response samlResponse)
    {
        var now = DateTime.UtcNow;

        // Drop entries for assertions that can no longer be valid anyway.
        foreach (var entry in SamlReplayCache)
        {
            if (now > entry.Value)
            {
                SamlReplayCache.TryRemove(entry.Key, out _);
            }
        }

        var assertionId = samlResponse.GetAssertionId();
        if (string.IsNullOrEmpty(assertionId))
        {
            // No usable assertion ID means single use cannot be guaranteed; reject to be safe.
            return false;
        }

        return SamlReplayCache.TryAdd(provider + "|" + assertionId, samlResponse.GetExpiry());
    }

    // Confirms the assertion was issued for this service provider when audience validation is enabled.
    private static bool AudienceAllowed(SamlConfig config, Response samlResponse)
    {
        if (!config.ValidateAudience)
        {
            return true;
        }

        var expected = config.SamlClientId?.Trim();
        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        return samlResponse.GetAudiences().Any(audience => string.Equals(audience?.Trim(), expected, StringComparison.Ordinal));
    }

    private string GetRequestBase(string schemeOverride = null, int? portOverride = null)
    {
        int requestPort;

        if (portOverride != null)
        {
            requestPort = portOverride.Value;
        }
        else
        {
            requestPort = Request.Host.Port ?? -1;
        }

        if ((requestPort == 80 && string.Equals(Request.Scheme, "http", StringComparison.OrdinalIgnoreCase)) || (requestPort == 443 && string.Equals(Request.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            requestPort = -1;
        }

        if (schemeOverride != "http" && schemeOverride != "https")
        {
            schemeOverride = null;
        }

        return new UriBuilder
        {
            Scheme = schemeOverride ?? Request.Scheme,
            Host = Request.Host.Host,
            Port = requestPort,
            Path = Request.PathBase
        }.ToString().TrimEnd('/');
    }

    private static string DiscoveryCacheKey(string provider, OidConfig config)
    {
        // Keyed on endpoint too, so changing the provider's endpoint invalidates the cache.
        return provider + "|" + (config.OidEndpoint?.Trim() ?? string.Empty);
    }

    // Reuses a previously-discovered OIDC document if it is still fresh, letting OidcClient skip discovery.
    private static bool TryApplyCachedDiscovery(OidcClientOptions options, string cacheKey)
    {
        if (DiscoveryCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.CachedAt < DiscoveryCacheTtl)
        {
            options.ProviderInformation = cached.Info;
            return true;
        }

        return false;
    }

    // Stores the discovery document that OidcClient populates on the options after a cache miss.
    private static void StoreDiscovery(OidcClientOptions options, string cacheKey)
    {
        if (options.ProviderInformation != null)
        {
            DiscoveryCache[cacheKey] = new CachedProviderInfo(options.ProviderInformation, DateTime.UtcNow);
        }
    }

    private ContentResult ReturnError(int code, string message)
    {
        var errorResult = new ContentResult();
        errorResult.Content = message;
        errorResult.ContentType = MediaTypeNames.Text.Plain;
        errorResult.StatusCode = code;
        return errorResult;
    }
}

/// <summary>
/// The data the client should pass back to the API.
/// </summary>
public class AuthResponse
{
    /// <summary>
    /// Gets or sets the device ID of the client.
    /// </summary>
    public string DeviceID { get; set; }

    /// <summary>
    /// Gets or sets the device name of the client.
    /// </summary>
    public string DeviceName { get; set; }

    /// <summary>
    /// Gets or sets the app name of the client.
    /// </summary>
    public string AppName { get; set; }

    /// <summary>
    /// Gets or sets the app version of the client.
    /// </summary>
    public string AppVersion { get; set; }

    /// <summary>
    /// Gets or sets the auth data of the client (for authorizing the response).
    /// </summary>
    public string Data { get; set; }
}

/// <summary>
/// A cached OIDC discovery document with the time it was fetched.
/// </summary>
internal sealed class CachedProviderInfo
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CachedProviderInfo"/> class.
    /// </summary>
    /// <param name="info">The discovered provider information.</param>
    /// <param name="cachedAt">When the information was cached.</param>
    public CachedProviderInfo(Duende.IdentityModel.OidcClient.ProviderInformation info, DateTime cachedAt)
    {
        Info = info;
        CachedAt = cachedAt;
    }

    /// <summary>
    /// Gets the discovered provider information.
    /// </summary>
    public Duende.IdentityModel.OidcClient.ProviderInformation Info { get; }

    /// <summary>
    /// Gets the time the information was cached.
    /// </summary>
    public DateTime CachedAt { get; }
}

/// <summary>
/// A manager for OpenID to manage the state of the clients.
/// </summary>
public class TimedAuthorizeState
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TimedAuthorizeState"/> class.
    /// </summary>
    /// <param name="state">The AuthorizeState to time.</param>
    /// <param name="created">When this state was created.</param>
    public TimedAuthorizeState(AuthorizeState state, DateTime created)
    {
        State = state;
        Created = created;
        Valid = false;
        Admin = false;
        IsLinking = false;
        EnableLiveTv = false;
        EnableLiveTvManagement = false;
        AvatarURL = null;
        SubjectId = null;
        Processed = false;
        ProcessError = null;
        ProcessLock = new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Gets or sets the Authorization State of the client.
    /// </summary>
    public AuthorizeState State { get; set; }

    /// <summary>
    /// Gets or sets when this object was created to time it out.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is valid.
    /// </summary>
    public bool Valid { get; set; }

    /// <summary>
    /// Gets or sets the user tied to the state.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// Gets or sets the immutable subject identifier (OIDC <c>sub</c> claim) used as the canonical link key.
    /// </summary>
    public string SubjectId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is an administrator.
    /// </summary>
    public bool Admin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the state is
    /// tied to a linking flow (instead of a login flow).
    /// </summary>
    public bool IsLinking { get; set; }

    /// <summary>
    /// Gets or sets the folders the user is allowed access to.
    /// </summary>
    public List<string> Folders { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is allowed to view live TV.
    /// </summary>
    public bool EnableLiveTv { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user is allowed to manage live TV.
    /// </summary>
    public bool EnableLiveTvManagement { get; set; }

    /// <summary>
    /// Gets or sets the user avatar url.
    /// </summary>
    public string AvatarURL { get; set; }

    /// <summary>
    /// Gets or sets the redirect URI used for this login, replayed during the deferred token exchange.
    /// </summary>
    public string RedirectUri { get; set; }

    /// <summary>
    /// Gets or sets the raw provider callback query string, stashed until the deferred token exchange.
    /// </summary>
    public string CallbackQuery { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the deferred token exchange has been attempted.
    /// </summary>
    public bool Processed { get; set; }

    /// <summary>
    /// Gets or sets the error message from the deferred token exchange, if any (null on success).
    /// </summary>
    public string ProcessError { get; set; }

    /// <summary>
    /// Gets the lock guarding the deferred token exchange so it runs exactly once.
    /// </summary>
    public SemaphoreSlim ProcessLock { get; private set; }
}
