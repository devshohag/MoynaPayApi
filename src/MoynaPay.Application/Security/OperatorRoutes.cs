namespace MoynaPay.Application.Security;

/// <summary>
/// The paths under /v1/ that are not signed, because the caller has no key yet.
///
/// A merchant that does not exist cannot sign for itself, so onboarding has to be reachable
/// some other way. "Some other way" was an exemption on the whole "/v1/merchants" prefix,
/// which also exempted the route that hands out that merchant's signing secret - so anyone
/// who had seen a merchant id could ask for the key and then sign as the shop. A merchant
/// id is not a credential: it appears in URLs, in logs, and in the body of a webhook.test
/// posted to somebody else's server.
///
/// These paths are gated on an operator credential instead. The list lives here, in one
/// place, rather than in the middleware and again in the routes: two copies of a rule like
/// this drift, and the drift is silent until somebody reads the access log.
/// </summary>
public static class OperatorRoutes
{
    public static bool IsOperatorOnly(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Creating a merchant.
        if (path.Equals("/v1/merchants", StringComparison.Ordinal)) return true;

        // Issuing that merchant's first key. Matched on the whole shape rather than on
        // "contains api-keys", so no other path can wander into the exemption.
        return path.StartsWith("/v1/merchants/", StringComparison.Ordinal)
            && path.EndsWith("/api-keys", StringComparison.Ordinal)
            && path.AsSpan("/v1/merchants/".Length).IndexOf('/')
                == path.Length - "/v1/merchants/".Length - "/api-keys".Length;
    }
}
