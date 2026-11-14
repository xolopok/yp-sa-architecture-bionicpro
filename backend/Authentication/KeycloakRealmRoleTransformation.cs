using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace BionicPro.Reports.Api.Authentication;

/// <summary>
/// Преобразует клейм realm_access ролей Keycloak в стандартные клеймы ролей,
/// чтобы авторизация ASP.NET Core проверяла их по ролям.
/// </summary>
public sealed class KeycloakRealmRoleTransformation : IClaimsTransformation
{
    /// <summary>
    /// Добавляет клейм роли для каждой realm-роли из клейма realm_access.
    /// </summary>
    /// <param name="principal">The authenticated claims principal.</param>
    /// <returns>The principal with the realm roles added as role claims.</returns>
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return Task.FromResult(principal);
        }

        var realmAccessJson = principal.FindFirst("realm_access")?.Value;
        if (string.IsNullOrEmpty(realmAccessJson))
        {
            return Task.FromResult(principal);
        }

        using var document = JsonDocument.Parse(realmAccessJson);

        if (!document.RootElement.TryGetProperty("roles", out var roles))
        {
            return Task.FromResult(principal);
        }

        foreach (var role in roles.EnumerateArray())
        {
            var roleName = role.GetString();
            if (!string.IsNullOrEmpty(roleName))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
            }
        }

        return Task.FromResult(principal);
    }
}
