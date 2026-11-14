namespace BionicPro.Reports.Api.Configuration;

/// <summary>
/// Предоставляет параметры подключения к ClickHouse для API отчётов.
/// </summary>
public sealed class ClickHouseOptions
{
    /// <summary>
    /// Возвращает или задает строку HTTP-подключения к ClickHouse.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
