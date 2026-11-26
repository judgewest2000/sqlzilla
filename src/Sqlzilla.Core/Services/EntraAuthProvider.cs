using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Sqlzilla.Services;

/// <summary>
/// Handles "Active Directory Interactive" sign-in through WAM (as SqlClient's own provider does) and delegates every
/// other Entra method to SqlClient. Owning the token cache lets background work, such as loading database lists for the
/// explorer search, reuse an account that's already signed in, while failing rather than showing a sign-in window.
/// </summary>
internal sealed class EntraAuthProvider(SqlAuthenticationProvider inner, Func<IntPtr> parentWindow) : SqlAuthenticationProvider
{
    // SqlClient's first-party app id; its registration allows the WAM redirect URI.
    private const string ClientId = "2fd908ad-0664-4344-b9be-cd3e8b574c38";

    private static readonly AsyncLocal<bool> s_silentOnly = new();
    private readonly ConcurrentDictionary<string, IPublicClientApplication> _apps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Connections opened in the current async flow never show a sign-in window.</summary>
    public static bool SilentOnly
    {
        get => s_silentOnly.Value;
        set => s_silentOnly.Value = value;
    }

    public override bool IsSupported(SqlAuthenticationMethod authenticationMethod) => inner.IsSupported(authenticationMethod);

    public override void BeforeLoad(SqlAuthenticationMethod authenticationMethod) => inner.BeforeLoad(authenticationMethod);

    public override void BeforeUnload(SqlAuthenticationMethod authenticationMethod) => inner.BeforeUnload(authenticationMethod);

    public override async Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters)
    {
        if (parameters.AuthenticationMethod != SqlAuthenticationMethod.ActiveDirectoryInteractive)
            return await inner.AcquireTokenAsync(parameters);

        // One app per sign-in host, with the tenant chosen per request: an account signed in for one server's tenant is
        // then visible to servers in other tenants too (separate apps per tenant each had their own account list).
        var (host, tenant) = SplitAuthority(parameters.Authority);
        var app = _apps.GetOrAdd(host, h => PublicClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority($"{h}/organizations")
            .WithRedirectUri("ms-appx-web://microsoft.aad.brokerplugin/" + ClientId)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .WithParentActivityOrWindow(() => parentWindow())
            .Build());
        var resource = parameters.Resource;
        string[] scopes = [resource.EndsWith("/.default", StringComparison.Ordinal) ? resource : resource + "/.default"];
        var hint = string.IsNullOrWhiteSpace(parameters.UserId) ? null : parameters.UserId;

        try
        {
            var accounts = (await app.GetAccountsAsync())
                .Where(a => hint == null || string.Equals(a.Username, hint, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // Background loads fall back to the account signed in to Windows: WAM can use it without showing a window.
            // Not when connecting for real, where the sign-in window lets you pick a different account.
            if (SilentOnly) accounts.Add(PublicClientApplication.OperatingSystemAccount);
            foreach (var account in accounts)
            {
                try
                {
                    var silent = await app.AcquireTokenSilent(scopes, account)
                        .WithTenantId(tenant)
                        .WithCorrelationId(parameters.ConnectionId)
                        .ExecuteAsync();
                    if (hint != null && !string.Equals(silent.Account?.Username, hint, StringComparison.OrdinalIgnoreCase)) continue;
                    return new SqlAuthenticationToken(silent.AccessToken, silent.ExpiresOn);
                }
                catch (MsalUiRequiredException) { }
                catch (MsalClientException) when (ReferenceEquals(account, PublicClientApplication.OperatingSystemAccount)) { }
            }
            if (SilentOnly) throw new SignInException("Not signed in; expand the server to sign in.");

            var result = await app.AcquireTokenInteractive(scopes)
                .WithTenantId(tenant)
                .WithCorrelationId(parameters.ConnectionId)
                .WithLoginHint(hint)
                .ExecuteAsync();
            return new SqlAuthenticationToken(result.AccessToken, result.ExpiresOn);
        }
        catch (MsalException ex)
        {
            throw new SignInException(ex.Message, ex);
        }
    }

    /// <summary>"https://login.windows.net/{tenant}/oauth2/authorize" becomes ("https://login.windows.net", "{tenant}").</summary>
    internal static (string Host, string Tenant) SplitAuthority(string authority)
    {
        var uri = new Uri(authority);
        var tenant = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "organizations";
        return ($"{uri.Scheme}://{uri.Authority}", tenant);
    }

    private sealed class SignInException(string message, Exception? causedBy = null)
        : SqlAuthenticationProviderException(SqlAuthenticationMethod.ActiveDirectoryInteractive, "SignInFailed", false, 0, message, causedBy);
}
