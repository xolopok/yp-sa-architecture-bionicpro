namespace BionicPro.Reports.Api.Configuration;

/// <summary>
/// Предоставляет параметры проверки JWT Keycloak для API отчётов.
/// </summary>
public sealed class KeycloakAuthOptions
{
    /// <summary>
    /// Возвращает или задает адрес OpenID Connect discovery для загрузки ключей подписи.
    /// </summary>
    public string MetadataAddress { get; set; } = string.Empty;

    /// <summary>
    /// Возвращает или задает допустимых издателей токена (для браузера и между контейнерами).
    /// </summary>
    public IReadOnlyList<string> ValidIssuers { get; set; } = [];

    /// <summary>
    /// Возвращает или задает единственный клиент (azp), которому разрешено обращаться к API.
    /// </summary>
    public string AllowedAzp { get; set; } = string.Empty;

    /// <summary>
    /// Возвращает или задает допуск часов при проверке времени жизни токена, в секундах.
    /// </summary>
    public int ClockSkewSeconds { get; set; } = 30;
}
