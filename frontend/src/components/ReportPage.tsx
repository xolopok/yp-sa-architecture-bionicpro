import React, { useState } from 'react';
import { useKeycloak } from '@react-keycloak/web';

interface DailyMetric {
  date: string;
  prosthesisId: string;
  modelName: string;
  totalEvents: number;
  successfulMovements: number;
  failedMovements: number;
  successPercent: number;
  avgResponseTimeMs: number;
  p95ResponseTimeMs: number;
  slowResponsesOver100Ms: number;
  avgMyoSignalMv: number;
  avgNoiseRatio: number;
  minBatteryPct: number;
  avgBatteryPct: number;
  maxBatteryTempC: number;
}

interface Report {
  username: string;
  from: string;
  to: string;
  client: { username: string; email: string; fullName: string };
  prostheses: { prosthesisId: string; modelName: string; chipVersion: string }[];
  periodSummary: {
    totalMovements: number;
    successfulMovements: number;
    successPercent: number;
    avgResponseTimeMs: number;
    slowResponsesOver100Ms: number;
    minBatteryPct: number;
  };
  dailyMetrics: DailyMetric[];
}

const ReportPage: React.FC = () => {
  const { keycloak, initialized } = useKeycloak();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [report, setReport] = useState<Report | null>(null);
  const [fromDate, setFromDate] = useState('');
  const [toDate, setToDate] = useState('');
  const [availableRange, setAvailableRange] = useState<string | null>(null);
  const [cacheStatus, setCacheStatus] = useState<string | null>(null);

  const buildQueryString = (format: string) => {
    const params = new URLSearchParams({ format });
    if (fromDate) params.set('from', fromDate);
    if (toDate) params.set('to', toDate);
    return params.toString();
  };

  const authorize = async () => {
    await keycloak.updateToken(30);
    if (!keycloak.token) {
      throw new Error('Not authenticated');
    }
    return keycloak.token as string;
  };

  const applyAvailableRange = (earliest: string | null, latest: string | null) => {
    if (earliest && latest) {
      setAvailableRange(`${earliest} .. ${latest}`);
    }
  };

  const handleResponseError = async (response: Response) => {
    if (response.status === 401) {
      setError('Session expired. Please log in again.');
      return;
    }
    if (response.status === 403) {
      setError('Access denied: reports are only available to authenticated users with the prothetic_user role, and only about yourself.');
      return;
    }
    if (response.status === 409) {
      let body: { earliestAvailableDate?: string; latestAvailableDate?: string } = {};
      try {
        body = await response.json();
      } catch {
        // оставляем сообщение по умолчанию, если тело не JSON
      }
      applyAvailableRange(body.earliestAvailableDate ?? null, body.latestAvailableDate ?? null);
      setError(
        `Airflow has not processed the requested period yet. Processed dates: ${body.earliestAvailableDate ?? '?'} .. ${body.latestAvailableDate ?? '?'}`
      );
      return;
    }
    setError(`Request failed with status ${response.status}.`);
  };

  const generateReport = async () => {
    setError(null);
    setReport(null);
    setCacheStatus(null);
    try {
      setLoading(true);
      const token = await authorize();
      const response = await fetch(
        `${process.env.REACT_APP_API_URL}/reports?${buildQueryString('json')}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );
      applyAvailableRange(
        response.headers.get('X-Report-Earliest-Date'),
        response.headers.get('X-Report-Latest-Date')
      );
      setCacheStatus(response.headers.get('X-Report-Cache'));
      if (!response.ok) {
        await handleResponseError(response);
        return;
      }
      setReport(await response.json());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'An error occurred');
    } finally {
      setLoading(false);
    }
  };

  const downloadCsv = async () => {
    setError(null);
    setCacheStatus(null);
    try {
      setLoading(true);
      const token = await authorize();
      const response = await fetch(
        `${process.env.REACT_APP_API_URL}/reports?${buildQueryString('csv')}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );
      applyAvailableRange(
        response.headers.get('X-Report-Earliest-Date'),
        response.headers.get('X-Report-Latest-Date')
      );
      setCacheStatus(response.headers.get('X-Report-Cache'));
      if (!response.ok) {
        await handleResponseError(response);
        return;
      }
      const disposition = response.headers.get('Content-Disposition') ?? '';
      const match = /filename="?([^";]+)"?/.exec(disposition);
      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = match ? match[1] : 'bionicpro-report.csv';
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);
      URL.revokeObjectURL(url);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'An error occurred');
    } finally {
      setLoading(false);
    }
  };

  if (!initialized) {
    return <div>Loading...</div>;
  }

  if (!keycloak.authenticated) {
    return (
      <div className="flex flex-col items-center justify-center min-h-screen bg-gray-100">
        <button
          onClick={() => keycloak.login()}
          className="px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600"
        >
          Login
        </button>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-100 py-8">
      <div className="max-w-4xl mx-auto p-8 bg-white rounded-lg shadow-md">
        <div className="flex items-center justify-between mb-6">
          <h1 className="text-2xl font-bold">Prosthesis Usage Report</h1>
          <div className="flex items-center gap-4">
            <span className="text-gray-600">
              {keycloak.tokenParsed?.preferred_username}
            </span>
            <button
              onClick={() => keycloak.logout()}
              className="px-3 py-1 bg-gray-200 rounded hover:bg-gray-300"
            >
              Logout
            </button>
          </div>
        </div>

        <div className="flex flex-wrap items-end gap-4 mb-6">
          <label className="flex flex-col text-sm text-gray-700">
            From
            <input
              type="date"
              value={fromDate}
              onChange={(e) => setFromDate(e.target.value)}
              className="mt-1 px-3 py-2 border rounded"
            />
          </label>
          <label className="flex flex-col text-sm text-gray-700">
            To
            <input
              type="date"
              value={toDate}
              onChange={(e) => setToDate(e.target.value)}
              className="mt-1 px-3 py-2 border rounded"
            />
          </label>
          <button
            onClick={generateReport}
            disabled={loading}
            className={`px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 ${
              loading ? 'opacity-50 cursor-not-allowed' : ''
            }`}
          >
            {loading ? 'Loading...' : 'Generate Report'}
          </button>
          <button
            onClick={downloadCsv}
            disabled={loading}
            className={`px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700 ${
              loading ? 'opacity-50 cursor-not-allowed' : ''
            }`}
          >
            Download CSV
          </button>
        </div>

        {availableRange && (
          <p className="mb-4 text-sm text-gray-600">
            Airflow processed dates: {availableRange}
            {cacheStatus ? ` (cache: ${cacheStatus})` : ''}
          </p>
        )}

        {error && (
          <div className="mt-4 p-4 bg-red-100 text-red-700 rounded">{error}</div>
        )}

        {report && (
          <div className="mt-6">
            <h2 className="text-lg font-semibold mb-2">Client</h2>
            <p className="mb-1">
              {report.client.fullName} ({report.client.username}, {report.client.email})
            </p>
            <div className="flex flex-wrap gap-2 mb-4">
              {report.prostheses.map((p) => (
                <span
                  key={p.prosthesisId}
                  className="px-3 py-1 bg-blue-100 text-blue-800 rounded-full text-sm"
                >
                  {p.prosthesisId} ({p.modelName}, {p.chipVersion})
                </span>
              ))}
            </div>

            <h2 className="text-lg font-semibold mb-2">
              Period {report.from} .. {report.to}
            </h2>
            <div className="grid grid-cols-2 md:grid-cols-3 gap-4 mb-6">
              <div className="p-3 bg-gray-50 rounded">
                <div className="text-sm text-gray-500">Total movements</div>
                <div className="text-xl">{report.periodSummary.totalMovements}</div>
              </div>
              <div className="p-3 bg-gray-50 rounded">
                <div className="text-sm text-gray-500">Success</div>
                <div className="text-xl">
                  {report.periodSummary.successPercent.toFixed(1)}%
                </div>
              </div>
              <div className="p-3 bg-gray-50 rounded">
                <div className="text-sm text-gray-500">Avg latency</div>
                <div className="text-xl">
                  {report.periodSummary.avgResponseTimeMs.toFixed(1)} ms
                </div>
              </div>
              <div className="p-3 bg-gray-50 rounded">
                <div className="text-sm text-gray-500">Responses &gt; 100 ms</div>
                <div className="text-xl">{report.periodSummary.slowResponsesOver100Ms}</div>
              </div>
              <div className="p-3 bg-gray-50 rounded">
                <div className="text-sm text-gray-500">Min battery</div>
                <div className="text-xl">{report.periodSummary.minBatteryPct}%</div>
              </div>
            </div>

            <h2 className="text-lg font-semibold mb-2">Daily metrics</h2>
            <div className="overflow-x-auto">
              <table className="w-full text-sm border-collapse">
                <thead>
                  <tr className="bg-gray-100">
                    <th className="border px-2 py-1 text-left">Date</th>
                    <th className="border px-2 py-1 text-left">Prosthesis</th>
                    <th className="border px-2 py-1 text-right">Events</th>
                    <th className="border px-2 py-1 text-right">Success</th>
                    <th className="border px-2 py-1 text-right">Avg ms</th>
                    <th className="border px-2 py-1 text-right">P95 ms</th>
                    <th className="border px-2 py-1 text-right">&gt;100 ms</th>
                    <th className="border px-2 py-1 text-right">Min batt</th>
                    <th className="border px-2 py-1 text-right">Max temp</th>
                  </tr>
                </thead>
                <tbody>
                  {report.dailyMetrics.map((m) => (
                    <tr key={`${m.date}-${m.prosthesisId}`} className="odd:bg-white even:bg-gray-50">
                      <td className="border px-2 py-1">{m.date}</td>
                      <td className="border px-2 py-1">{m.prosthesisId}</td>
                      <td className="border px-2 py-1 text-right">{m.totalEvents}</td>
                      <td className="border px-2 py-1 text-right">
                        {m.successPercent.toFixed(1)}%
                      </td>
                      <td className="border px-2 py-1 text-right">
                        {m.avgResponseTimeMs.toFixed(1)}
                      </td>
                      <td className="border px-2 py-1 text-right">{m.p95ResponseTimeMs}</td>
                      <td className="border px-2 py-1 text-right">{m.slowResponsesOver100Ms}</td>
                      <td className="border px-2 py-1 text-right">{m.minBatteryPct}%</td>
                      <td className="border px-2 py-1 text-right">
                        {m.maxBatteryTempC.toFixed(1)}&deg;C
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    </div>
  );
};

export default ReportPage;
