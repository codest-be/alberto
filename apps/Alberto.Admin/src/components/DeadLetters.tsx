import { gql } from "@apollo/client";
import { useMutation, useQuery } from "@apollo/client/react";

// TODO: Replace with generated hooks from `npm run codegen`

const DEAD_LETTERS = gql`
  query GetDeadLetters($limit: Int) {
    adminDeadLetters(limit: $limit) {
      id
      processorId
      eventType
      globalPosition
      errorMessage
      failedAt
      tenantId
    }
  }
`;

const DEAD_LETTER_COUNT = gql`
  query GetDeadLetterCount {
    adminDeadLetterCount
  }
`;

const CLEAR_ALL = gql`
  mutation ClearAllDeadLetters {
    adminClearAllDeadLetters {
      deletedCount
    }
  }
`;

const RETRY_BY_REWIND = gql`
  mutation RetryByRewind($processorId: String!) {
    adminRetryByRewind(processorId: $processorId) {
      processorId
      rewindPosition
      deletedCount
    }
  }
`;

interface DeadLetterRow {
  id: string;
  processorId: string;
  eventType: string | null;
  globalPosition: number | null;
  errorMessage: string | null;
  failedAt: string | null;
  tenantId: string | null;
}

export function DeadLetters() {
  const { data, loading, error } = useQuery(DEAD_LETTERS, {
    variables: { limit: 50 },
    pollInterval: 5000,
  });
  const { data: countData } = useQuery(DEAD_LETTER_COUNT, {
    pollInterval: 5000,
  });
  const [clearAll, { loading: clearing }] = useMutation(CLEAR_ALL, {
    refetchQueries: [{ query: DEAD_LETTERS }, { query: DEAD_LETTER_COUNT }],
  });
  const [retryByRewind] = useMutation(RETRY_BY_REWIND, {
    refetchQueries: [{ query: DEAD_LETTERS }, { query: DEAD_LETTER_COUNT }],
  });

  if (loading) return <div className="loading">Loading…</div>;
  if (error) return <div className="error-msg">{error.message}</div>;

  const letters = ((data as any)?.adminDeadLetters ?? []) as DeadLetterRow[];
  const totalCount = ((countData as any)?.adminDeadLetterCount ?? letters.length) as number;

  // Group by processor for retry-by-rewind
  const processors = [...new Set(letters.map((dl) => dl.processorId))];

  return (
    <div className="card" style={{ gridColumn: "1 / -1" }}>
      <div className="card-header">
        <h2>Dead Letters</h2>
        <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <span
            className="badge"
            style={
              totalCount > 0
                ? { background: "rgba(239,68,68,0.15)", color: "var(--danger)" }
                : {}
            }
          >
            {totalCount}
          </span>
          {totalCount > 0 && (
            <button
              className="btn btn-danger btn-sm"
              disabled={clearing}
              onClick={() => {
                if (confirm(`Clear all ${totalCount} dead letters?`)) clearAll();
              }}
            >
              {clearing ? "Clearing…" : "Clear All"}
            </button>
          )}
        </div>
      </div>

      {processors.length > 0 && (
        <div style={{ display: "flex", gap: 8, marginBottom: 12, flexWrap: "wrap" }}>
          {processors.map((pid) => (
            <button
              key={pid}
              className="btn btn-sm"
              onClick={() => {
                if (
                  confirm(
                    `Retry dead letters for ${pid} by rewinding its checkpoint?`,
                  )
                ) {
                  retryByRewind({ variables: { processorId: pid } });
                }
              }}
            >
              Retry {pid}
            </button>
          ))}
        </div>
      )}

      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Processor</th>
              <th>Event Type</th>
              <th>Position</th>
              <th>Error</th>
              <th>Failed At</th>
              <th>Tenant</th>
            </tr>
          </thead>
          <tbody>
            {letters.length === 0 ? (
              <tr>
                <td colSpan={6} className="empty">
                  No dead letters 🎉
                </td>
              </tr>
            ) : (
              letters.map((dl) => (
                <tr key={dl.id}>
                  <td>
                    <code>{dl.processorId}</code>
                  </td>
                  <td>
                    <code>{dl.eventType ?? "—"}</code>
                  </td>
                  <td>{dl.globalPosition?.toLocaleString() ?? "—"}</td>
                  <td
                    style={{
                      maxWidth: 300,
                      overflow: "hidden",
                      textOverflow: "ellipsis",
                      whiteSpace: "nowrap",
                    }}
                    title={dl.errorMessage ?? undefined}
                  >
                    {dl.errorMessage ?? "—"}
                  </td>
                  <td>
                    {dl.failedAt
                      ? new Date(dl.failedAt).toLocaleTimeString()
                      : "—"}
                  </td>
                  <td>{dl.tenantId ?? "—"}</td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}
