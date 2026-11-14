namespace BionicPro.Reports.Api.Configuration;

/// <summary>
/// Предоставляет параметры S3-хранилища для кэша артефактов отчётов.
/// </summary>
public sealed class S3Options
{
    /// <summary>
    /// Возвращает или задает хост и порт S3-эндпоинта.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Возвращает или задает ключ доступа S3.
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Возвращает или задает регион подписи объектного хранилища S3.
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Возвращает или задает секретный ключ S3.
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Возвращает или задает бакет с кэшированными артефактами отчётов.
    /// </summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// Возвращает или задает значение, указывающее, выполнять ли подключение через TLS.
    /// </summary>
    public bool UseSsl { get; set; }
}