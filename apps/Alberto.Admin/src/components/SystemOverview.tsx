import { gql } from "@apollo/client";
import { useQuery, useSubscription } from "@apollo/client/react";

// TODO: Replace with generated hooks from `npm run codegen` once the server is running.
// The codegen output provides fully typed hooks that eliminate the `as any` casts below.

const SYSTEM_INFO = gql`
  query GetSystemInfo {
    adminSystemInfo {
      globalPosition
      processorCount
      deadLetterCount
      lastEventAt
    }
  }
`;

const ON_SYSTEM_UPDATED = gql`
  subscription OnSystemUpdated {
    onAdminSystemUpdated {
      globalPosition
      processorCount
      deadLetterCount
      lastEventAt
    }
  }
`;

interface SystemInfoData {
  globalPosition: number;
  processorCount: number;
  deadLetterCount: number;
  lastEventAt: string | null;
}

export function SystemOverview() {
  const { data, loading, error } = useQuery(SYSTEM_INFO, {
    pollInterval: 5000,
  });

  useSubscription(ON_SYSTEM_UPDATED, {
    onData: ({ client, data: subData }) => {
      const updated = (subData as any).data?.onAdminSystemUpdated;
      if (updated) {
        client.writeQuery({
          query: SYSTEM_INFO,
          data: { adminSystemInfo: updated },
        });
      }
    },
  });

  if (loading) return <div className="loading">Loading…</div>;
  if (error) return <div className="error-msg">{error.message}</div>;

  const info = (data as any)?.adminSystemInfo as SystemInfoData | undefined;
  if (!info) return <div className="empty">No system info available</div>;

  return (
    <div className="card">
      <div className="card-header">
        <h2>System Overview</h2>
        <span className="badge">Live</span>
      </div>
      <div className="stat-grid">
        <div className="stat">
          <div className="stat-value">{formatNumber(info.globalPosition)}</div>
          <div className="stat-label">Global Position</div>
        </div>
        <div className="stat">
          <div className="stat-value">{info.processorCount}</div>
          <div className="stat-label">Processors</div>
        </div>
        <div className="stat">
          <div
            className={`stat-value ${info.deadLetterCount > 0 ? "danger" : ""}`}
          >
            {info.deadLetterCount}
          </div>
          <div className="stat-label">Dead Letters</div>
        </div>
      </div>
    </div>
  );
}

function formatNumber(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n ?? 0);
}
