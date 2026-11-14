using BionicPro.Reports.Api.Configuration;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace BionicPro.Reports.Api.Services;

/// <summary>
/// Кэширует неизменяемые артефакты отчётов в S3-совместимом объектном
/// хранилище (Garage) через стандартный S3-клиент.
/// </summary>
public sealed class S3ReportCache
{
    private readonly MinioClient client;
    private readonly string bucketName;

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="S3ReportCache"/>.
    /// </summary>
    /// <param name="options">Параметры подключения к S3-хранилищу.</param>
    public S3ReportCache(IOptions<S3Options> options)
    {
        var value = options.Value;
        client = (MinioClient)new MinioClient()
            .WithEndpoint(value.Endpoint)
            .WithCredentials(value.AccessKey, value.SecretKey)
            .WithRegion(value.Region)
            .WithSSL(value.UseSsl)
            .Build();
        bucketName = value.BucketName;
    }

    /// <summary>
    /// Создает бакет артефактов отчётов, если он еще не существует.
    /// </summary>
    public async Task EnsureBucketExistsAsync()
    {
        var exists = await client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(bucketName));
        if (!exists)
        {
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucketName));
        }
    }

    /// <summary>
    /// Пытается получить кэшированный артефакт отчёта.
    /// </summary>
    /// <param name="objectName">Ключ объекта артефакта.</param>
    /// <returns>Байты артефакта или null, если объект не закэширован.</returns>
    public async Task<byte[]?> GetAsync(string objectName)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            await client.GetObjectAsync(new GetObjectArgs()
                .WithBucket(bucketName)
                .WithObject(objectName)
                .WithCallbackStream(stream => stream.CopyTo(memoryStream)));
            return memoryStream.ToArray();
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Сохраняет артефакт отчёта в бакете кэша.
    /// </summary>
    /// <param name="objectName">Ключ объекта артефакта.</param>
    /// <param name="content">Байты артефакта.</param>
    /// <param name="contentType">Тип содержимого артефакта.</param>
    public async Task PutAsync(string objectName, byte[] content, string contentType)
    {
        await using var stream = new MemoryStream(content);
        await client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(bucketName)
            .WithObject(objectName)
            .WithStreamData(stream)
            .WithObjectSize(content.Length)
            .WithContentType(contentType));
    }
}