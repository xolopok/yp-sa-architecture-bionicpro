using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using BionicPro.Reports.Api.Authentication;
using BionicPro.Reports.Api.Configuration;
using BionicPro.Reports.Api.Models;
using BionicPro.Reports.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var keycloakOptions = builder.Configuration
    .GetSection(KeycloakAuthOptionsExtensions.SectionName)
    .Get<KeycloakAuthOptions>() ?? new KeycloakAuthOptions();

builder.Services.AddOptions<ClickHouseOptions>()
    .Bind(builder.Configuration.GetSection("ClickHouse"));
builder.Services.AddOptions<S3Options>()
    .Bind(builder.Configuration.GetSection("S3"));

builder.Services.AddScoped<IClaimsTransformation, KeycloakRealmRoleTransformation>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MetadataAddress = keycloakOptions.MetadataAddress;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            ValidateIssuer = true,
            ValidIssuers = keycloakOptions.ValidIssuers,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(keycloakOptions.ClockSkewSeconds),
            NameClaimType = "preferred_username",
            RoleClaimType = ClaimTypes.Role,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                // Аудитория проверяется по azp: к API разрешен только клиент
                // PKCE-фронтенда. Клиент reports-api -- bearer-only и вход
                // не инициирует, поэтому в azp попасть не может.
                var azp = context.Principal?.FindFirst("azp")?.Value;
                if (!string.Equals(azp, keycloakOptions.AllowedAzp, StringComparison.Ordinal))
                {
                    context.Fail(
                        $"Token issued for unauthorized client '{(azp ?? "<missing>")}'.");
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ProstheticReportPolicy", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("prothetic_user");
    });
});

builder.Services.AddScoped<ClickHouseReportRepository>();
builder.Services.AddSingleton<S3ReportCache>();
builder.Services.AddSingleton<CsvReportFormatter>();

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? ["http://localhost:3002"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .WithMethods("GET", "OPTIONS")
            .WithHeaders("Authorization", "Content-Type")
            .WithExposedHeaders(
                "Content-Disposition",
                "X-Report-Earliest-Date",
                "X-Report-Latest-Date",
                "X-Report-Cache");
    });
});

var app = builder.Build();

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

var s3Cache = app.Services.GetRequiredService<S3ReportCache>();
await s3Cache.EnsureBucketExistsAsync();

var jsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/reports",
    async (string? from, string? to, string? format, string? username,
        ClaimsPrincipal principal, HttpResponse response,
        ClickHouseReportRepository repository, S3ReportCache cache,
        CsvReportFormatter csvFormatter) =>
    await GetReportsAsync(
        from, to, format, username, principal, response,
        repository, cache, csvFormatter))
    .RequireAuthorization("ProstheticReportPolicy");

app.Run();

async Task<IResult> GetReportsAsync(
    string? fromParameter,
    string? toParameter,
    string? formatParameter,
    string? usernameParameter,
    ClaimsPrincipal principal,
    HttpResponse response,
    ClickHouseReportRepository repository,
    S3ReportCache cache,
    CsvReportFormatter csvFormatter)
{
    // Отчёт выдается только о самом себе: владелец берется из проверенного
    // токена, параметр с чужим именем отклоняется сразу.
    var caller = principal.FindFirst("preferred_username")?.Value;
    if (string.IsNullOrWhiteSpace(caller))
    {
        return Results.Json(
            new { code = "UNAUTHENTICATED", message = "Missing verified identity in the token." },
            jsonSerializerOptions,
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (!string.IsNullOrEmpty(usernameParameter) &&
        !string.Equals(usernameParameter, caller, StringComparison.Ordinal))
    {
        return Results.Json(
            new
            {
                code = "ACCESS_DENIED",
                message = $"Users can only request their own reports; you are '{caller}'.",
            },
            jsonSerializerOptions,
            statusCode: StatusCodes.Status403Forbidden);
    }

    var format = string.IsNullOrEmpty(formatParameter) ? "json" : formatParameter;
    if (format is not ("json" or "csv"))
    {
        return Results.Json(
            new { code = "INVALID_FORMAT", message = "format must be 'json' or 'csv'." },
            jsonSerializerOptions,
            statusCode: StatusCodes.Status400BadRequest);
    }

    DateOnly? from = null;
    DateOnly? to = null;
    if (!string.IsNullOrEmpty(fromParameter) &&
        (!DateOnly.TryParse(fromParameter, out var parsedFrom) ||
         (from = parsedFrom) is null))
    {
        return Results.Json(
            new { code = "INVALID_DATE", message = "from must be a valid YYYY-MM-DD date." },
            jsonSerializerOptions,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (!string.IsNullOrEmpty(toParameter) &&
        (!DateOnly.TryParse(toParameter, out var parsedTo) ||
         (to = parsedTo) is null))
    {
        return Results.Json(
            new { code = "INVALID_DATE", message = "to must be a valid YYYY-MM-DD date." },
            jsonSerializerOptions,
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (from > to)
    {
        return Results.Json(
            new { code = "INVALID_RANGE", message = "from must not be later than to." },
            jsonSerializerOptions,
            statusCode: StatusCodes.Status400BadRequest);
    }

    // Границы отметок Airflow всегда читаются из ClickHouse начисто --
    // и для проверок 409, и для заголовков; в S3-артефактах их нет.
    var summary = await repository.GetProcessedDatesSummaryAsync();
    if (summary.Count == 0)
    {
        return Results.Json(
            new
            {
                code = "ETL_NOT_READY",
                message = "Airflow has not completed initial report data preparation yet.",
            },
            jsonSerializerOptions,
            statusCode: StatusCodes.Status409Conflict);
    }

    var effectiveTo = to ?? summary.Latest;
    var defaultFrom = effectiveTo.AddDays(-6);
    var effectiveFrom = from ?? (summary.Earliest > defaultFrom ? summary.Earliest : defaultFrom);
    if (effectiveFrom > effectiveTo)
    {
        return Results.Json(
            new { code = "INVALID_RANGE", message = "from must not be later than to." },
            jsonSerializerOptions,
            statusCode: StatusCodes.Status400BadRequest);
    }

    var periodNotProcessed = Results.Json(
        new
        {
            code = "PERIOD_NOT_PROCESSED",
            message = "The requested period contains days that Airflow has not processed yet.",
            earliestAvailableDate = summary.Earliest.ToString("yyyy-MM-dd"),
            latestAvailableDate = summary.Latest.ToString("yyyy-MM-dd"),
        },
        jsonSerializerOptions,
        statusCode: StatusCodes.Status409Conflict);

    if (effectiveFrom < summary.Earliest || effectiveTo > summary.Latest)
    {
        return periodNotProcessed;
    }

    var expectedDays = effectiveTo.DayNumber - effectiveFrom.DayNumber + 1;
    var processedDays = await repository.CountProcessedDatesInRangeAsync(effectiveFrom, effectiveTo);
    if (processedDays != expectedDays)
    {
        return periodNotProcessed;
    }

    var contentType = format == "csv"
        ? "text/csv; charset=utf-8"
        : "application/json; charset=utf-8";
    var objectName = $"reports/{caller}/{effectiveFrom:yyyy-MM-dd}_{effectiveTo:yyyy-MM-dd}.{format}";

    var cachedBytes = await cache.GetAsync(objectName);
    byte[] artifactBytes;
    if (cachedBytes is not null)
    {
        artifactBytes = cachedBytes;
    }
    else
    {
        var rows = await repository.GetMartRowsAsync(caller, effectiveFrom, effectiveTo);
        byte[] reportBytes;
        if (format == "csv")
        {
            if (rows.Count == 0)
            {
                reportBytes = csvFormatter.Format(EmptyReport(caller, effectiveFrom, effectiveTo));
            }
            else
            {
                reportBytes = csvFormatter.Format(
                    ReportFactory.Build(rows, effectiveFrom, effectiveTo));
            }
        }
        else
        {
            var report = rows.Count == 0
                ? EmptyReport(caller, effectiveFrom, effectiveTo)
                : ReportFactory.Build(rows, effectiveFrom, effectiveTo);
            reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, jsonSerializerOptions);
        }

        // Агрегаты завершенных дней не меняются, поэтому артефакт,
        // записанный при первом промахе, переиспользуется дальше.
        await cache.PutAsync(objectName, reportBytes, contentType);
        artifactBytes = reportBytes;
    }

    response.Headers["X-Report-Earliest-Date"] = summary.Earliest.ToString("yyyy-MM-dd");
    response.Headers["X-Report-Latest-Date"] = summary.Latest.ToString("yyyy-MM-dd");
    response.Headers["X-Report-Cache"] = cachedBytes is not null ? "HIT" : "MISS";
    if (format == "csv")
    {
        response.Headers["Content-Disposition"] =
            $"attachment; filename=\"bionicpro-report-{caller}-{effectiveFrom:yyyy-MM-dd}-{effectiveTo:yyyy-MM-dd}.csv\"";
    }

    return Results.Bytes(artifactBytes, contentType);
}

static CachedReport EmptyReport(string username, DateOnly from, DateOnly to) => new(
    username,
    from,
    to,
    new ReportClient(username, 0, string.Empty, string.Empty),
    [],
    new ReportPeriodSummary(0, 0, 0, 0, 0, 0),
    []);

file static class KeycloakAuthOptionsExtensions
{
    public const string SectionName = "Keycloak";
}
