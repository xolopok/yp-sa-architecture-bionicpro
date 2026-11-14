using BionicPro.Reports.Api.Configuration;
using BionicPro.Reports.Api.Models;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Utility;
using Microsoft.Extensions.Options;

namespace BionicPro.Reports.Api.Services;

/// <summary>
/// Читает предагрегированные данные отчётов и отметки ETL из ClickHouse.
/// </summary>
public sealed class ClickHouseReportRepository
{
    private readonly string connectionString;

    /// <summary>
    /// Инициализирует новый экземпляр класса <see cref="ClickHouseReportRepository"/>.
    /// </summary>
    /// <param name="options">Параметры подключения к ClickHouse.</param>
    public ClickHouseReportRepository(IOptions<ClickHouseOptions> options)
    {
        connectionString = options.Value.ConnectionString;
    }

    /// <summary>
    /// Возвращает сводку дат, обработанных ETL-пайплайном Airflow.
    /// </summary>
    /// <returns>Количество обработанных дат, а также самая ранняя и самая поздняя из них.</returns>
    public async Task<ProcessedDatesSummary> GetProcessedDatesSummaryAsync()
    {
        await using var connection = new ClickHouseConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(), min(report_date), max(report_date)
            FROM bionicpro_olap.etl_processed_dates FINAL
            WHERE pipeline_name = {pipeline:String}
            """;
        command.AddParameter("pipeline", "bionicpro_reports_etl");

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return new ProcessedDatesSummary(0, DateOnly.MinValue, DateOnly.MinValue);
        }

        var count = Convert.ToInt32(reader.GetFieldValue<ulong>(0));
        var earliest = reader.IsDBNull(1) ? DateOnly.MinValue : ToDateOnly(reader.GetDateTime(1));
        var latest = reader.IsDBNull(2) ? DateOnly.MinValue : ToDateOnly(reader.GetDateTime(2));
        return new ProcessedDatesSummary(count, earliest, latest);
    }

    /// <summary>
    /// Считает дни, обработанные Airflow, внутри включительного диапазона дат.
    /// </summary>
    /// <param name="from">Начальная дата диапазона.</param>
    /// <param name="to">Конечная дата диапазона.</param>
    /// <returns>Количество обработанных дней внутри диапазона.</returns>
    public async Task<int> CountProcessedDatesInRangeAsync(DateOnly from, DateOnly to)
    {
        await using var connection = new ClickHouseConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count()
            FROM bionicpro_olap.etl_processed_dates FINAL
            WHERE pipeline_name = {pipeline:String}
              AND report_date >= {dateFrom:Date}
              AND report_date <= {dateTo:Date}
            """;
        command.AddParameter("pipeline", "bionicpro_reports_etl");
        command.AddParameter("dateFrom", from);
        command.AddParameter("dateTo", to);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result ?? 0UL);
    }

    /// <summary>
    /// Возвращает предагрегированные строки витрины одного пользователя за закрытый период.
    /// </summary>
    /// <param name="username">Проверенное имя пользователя из JWT.</param>
    /// <param name="from">Эффективная начальная дата периода.</param>
    /// <param name="to">Эффективная конечная дата периода.</param>
    /// <returns>Строки витрины пользователя, упорядоченные по дате и протезу.</returns>
    public async Task<IReadOnlyList<MartRow>> GetMartRowsAsync(
        string username, DateOnly from, DateOnly to)
    {
        await using var connection = new ClickHouseConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                username, customer_id, email, full_name,
                prosthesis_id, model_name, chip_version, report_date,
                total_events, successful_movements, failed_movements,
                avg_response_time_ms, p95_response_time_ms, slow_responses_over_100ms,
                avg_myo_signal_mv, avg_noise_ratio,
                min_battery_pct, avg_battery_pct, max_battery_temp_c
            FROM bionicpro_olap.prosthesis_reports_mart FINAL
            WHERE username = {username:String}
              AND report_date >= {dateFrom:Date}
              AND report_date <= {dateTo:Date}
            ORDER BY report_date ASC, prosthesis_id ASC
            """;
        command.AddParameter("username", username);
        command.AddParameter("dateFrom", from);
        command.AddParameter("dateTo", to);

        var rows = new List<MartRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new MartRow(
                reader.GetString(0),
                Convert.ToInt32(reader.GetValue(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                ToDateOnly(reader.GetDateTime(7)),
                Convert.ToInt64(reader.GetValue(8)),
                Convert.ToInt64(reader.GetValue(9)),
                Convert.ToInt64(reader.GetValue(10)),
                Convert.ToDouble(reader.GetValue(11)),
                Convert.ToDouble(reader.GetValue(12)),
                Convert.ToInt64(reader.GetValue(13)),
                Convert.ToDouble(reader.GetValue(14)),
                Convert.ToDouble(reader.GetValue(15)),
                Convert.ToInt32(reader.GetValue(16)),
                Convert.ToDouble(reader.GetValue(17)),
                Convert.ToDouble(reader.GetValue(18))));
        }

        return rows;
    }

    private static DateOnly ToDateOnly(DateTime value) => DateOnly.FromDateTime(value);
}
