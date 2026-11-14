using System.Text.Json.Serialization;

namespace BionicPro.Reports.Api.Models;

/// <summary>
/// Представляет одну предагрегированную строку витрины отчётности ClickHouse.
/// </summary>
public sealed record MartRow(
    string Username,
    int CustomerId,
    string Email,
    string FullName,
    string ProsthesisId,
    string ModelName,
    string ChipVersion,
    DateOnly ReportDate,
    long TotalEvents,
    long SuccessfulMovements,
    long FailedMovements,
    double AvgResponseTimeMs,
    double P95ResponseTimeMs,
    long SlowResponsesOver100Ms,
    double AvgMyoSignalMv,
    double AvgNoiseRatio,
    int MinBatteryPct,
    double AvgBatteryPct,
    double MaxBatteryTempC);

/// <summary>
/// Представляет сводку отметок обработанных дат ETL.
/// </summary>
public sealed record ProcessedDatesSummary(int Count, DateOnly Earliest, DateOnly Latest);

/// <summary>
/// Представляет блок клиента в отчёте.
/// </summary>
public sealed record ReportClient(
    string Username,
    int CustomerId,
    string Email,
    string FullName);

/// <summary>
/// Представляет протез, описываемый в отчёте.
/// </summary>
public sealed record ReportProsthesis(
    string ProsthesisId,
    string ModelName,
    string ChipVersion);

/// <summary>
/// Представляет итоговые метрики периода в отчёте.
/// </summary>
public sealed record ReportPeriodSummary(
    long TotalMovements,
    long SuccessfulMovements,
    double SuccessPercent,
    double AvgResponseTimeMs,
    long SlowResponsesOver100Ms,
    int MinBatteryPct);

/// <summary>
/// Представляет метрики отчёта на каждый день и протез.
/// </summary>
public sealed record DailyMetric(
    DateOnly Date,
    string ProsthesisId,
    string ModelName,
    long TotalEvents,
    long SuccessfulMovements,
    long FailedMovements,
    double SuccessPercent,
    double AvgResponseTimeMs,
    double P95ResponseTimeMs,
    long SlowResponsesOver100Ms,
    double AvgMyoSignalMv,
    double AvgNoiseRatio,
    int MinBatteryPct,
    double AvgBatteryPct,
    double MaxBatteryTempC);

/// <summary>
/// Представляет неизменяемый артефакт отчёта, кэшируемый в S3 и выдаваемый
/// API. Изменчивые значения, растущие с каждым запуском Airflow (границы
/// обработанных дат), в него намеренно не входят и передаются в заголовках
/// каждого ответа.
/// </summary>
public sealed record CachedReport(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("from")] DateOnly From,
    [property: JsonPropertyName("to")] DateOnly To,
    [property: JsonPropertyName("client")] ReportClient Client,
    [property: JsonPropertyName("prostheses")] IReadOnlyList<ReportProsthesis> Prostheses,
    [property: JsonPropertyName("periodSummary")] ReportPeriodSummary PeriodSummary,
    [property: JsonPropertyName("dailyMetrics")] IReadOnlyList<DailyMetric> DailyMetrics);

/// <summary>
/// Предоставляет методы сборки артефактов отчётов из предагрегированных строк витрины.
/// </summary>
public static class ReportFactory
{
    /// <summary>
    /// Создает кэшируемый артефакт отчёта из строк витрины за закрытый период.
    /// </summary>
    /// <param name="rows">Строки витрины одного пользователя, упорядоченные по дате.</param>
    /// <param name="from">Эффективная начальная дата периода.</param>
    /// <param name="to">Эффективная конечная дата периода.</param>
    /// <returns>Собранный артефакт отчёта.</returns>
    /// <exception cref="ArgumentException">
    /// Выбрасывается, если <paramref name="rows"/> пуст или содержит более одного пользователя.
    /// </exception>
    public static CachedReport Build(IEnumerable<MartRow> rows, DateOnly from, DateOnly to)
    {
        var rowList = rows as IReadOnlyList<MartRow> ?? rows.ToList();
        if (rowList.Count == 0)
        {
            throw new ArgumentException("No mart rows for the requested period.", nameof(rows));
        }

        var usernames = rowList.Select(r => r.Username).Distinct().ToList();
        if (usernames.Count > 1)
        {
            throw new ArgumentException("Mart rows must belong to a single user.", nameof(rows));
        }

        var first = rowList[0];
        var client = new ReportClient(first.Username, first.CustomerId, first.Email, first.FullName);
        var prostheses = rowList
            .Select(r => new ReportProsthesis(r.ProsthesisId, r.ModelName, r.ChipVersion))
            .DistinctBy(p => p.ProsthesisId)
            .ToList();

        var totalMovements = rowList.Sum(r => r.TotalEvents);
        var successfulMovements = rowList.Sum(r => r.SuccessfulMovements);
        var slowResponses = rowList.Sum(r => r.SlowResponsesOver100Ms);
        var avgResponse = totalMovements > 0
            ? rowList.Sum(r => r.AvgResponseTimeMs * r.TotalEvents) / totalMovements
            : 0;

        var periodSummary = new ReportPeriodSummary(
            totalMovements,
            successfulMovements,
            totalMovements > 0 ? successfulMovements * 100.0 / totalMovements : 0,
            Math.Round(avgResponse, 2),
            slowResponses,
            rowList.Count > 0 ? rowList.Min(r => r.MinBatteryPct) : 0);

        var dailyMetrics = rowList
            .Select(r => new DailyMetric(
                r.ReportDate,
                r.ProsthesisId,
                r.ModelName,
                r.TotalEvents,
                r.SuccessfulMovements,
                r.FailedMovements,
                r.TotalEvents > 0 ? r.SuccessfulMovements * 100.0 / r.TotalEvents : 0,
                Math.Round(r.AvgResponseTimeMs, 2),
                Math.Round(r.P95ResponseTimeMs, 2),
                r.SlowResponsesOver100Ms,
                Math.Round(r.AvgMyoSignalMv, 2),
                Math.Round(r.AvgNoiseRatio, 3),
                r.MinBatteryPct,
                Math.Round(r.AvgBatteryPct, 2),
                Math.Round(r.MaxBatteryTempC, 1)))
            .OrderBy(m => m.Date)
            .ThenBy(m => m.ProsthesisId, StringComparer.Ordinal)
            .ToList();

        return new CachedReport(
            first.Username,
            from,
            to,
            client,
            prostheses,
            periodSummary,
            dailyMetrics);
    }
}
