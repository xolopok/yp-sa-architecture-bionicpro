using System.Globalization;
using BionicPro.Reports.Api.Models;

namespace BionicPro.Reports.Api.Services;

/// <summary>
/// Форматирует кэшируемые артефакты отчётов в документы CSV.
/// </summary>
public sealed class CsvReportFormatter
{
    private const string Separator = ";";
    private const string Quote = "\"";

    /// <summary>
    /// Преобразует артефакт отчёта в CSV-документ со строкой на каждый день
    /// и протез.
    /// </summary>
    /// <param name="report">Артефакт отчёта.</param>
    /// <returns>Байты CSV-документа в кодировке UTF-8.</returns>
    public byte[] Format(CachedReport report)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine(string.Join(Separator,
            "date", "prosthesis_id", "model", "total_events", "successful_movements",
            "failed_movements", "success_percent", "avg_response_time_ms",
            "p95_response_time_ms", "slow_responses_over_100ms", "avg_myo_signal_mv",
            "avg_noise_ratio", "min_battery_pct", "avg_battery_pct", "max_battery_temp_c"));

        foreach (var metric in report.DailyMetrics)
        {
            writer.WriteLine(string.Join(Separator,
                metric.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Quote + metric.ProsthesisId + Quote,
                Quote + metric.ModelName + Quote,
                metric.TotalEvents,
                metric.SuccessfulMovements,
                metric.FailedMovements,
                FormattableString.Invariant($"{metric.SuccessPercent:0.##}"),
                FormattableString.Invariant($"{metric.AvgResponseTimeMs:0.##}"),
                FormattableString.Invariant($"{metric.P95ResponseTimeMs:0.##}"),
                metric.SlowResponsesOver100Ms,
                FormattableString.Invariant($"{metric.AvgMyoSignalMv:0.##}"),
                FormattableString.Invariant($"{metric.AvgNoiseRatio:0.###}"),
                metric.MinBatteryPct,
                FormattableString.Invariant($"{metric.AvgBatteryPct:0.##}"),
                FormattableString.Invariant($"{metric.MaxBatteryTempC:0.#}")));
        }

        return System.Text.Encoding.UTF8.GetBytes(writer.ToString());
    }
}
