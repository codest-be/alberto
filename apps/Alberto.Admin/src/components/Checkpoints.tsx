import { gql } from "@apollo/client";
import { useMutation, useQuery } from "@apollo/client/react";

// TODO: Replace with generated hooks from `npm run codegen`

const CHECKPOINTS = gql`
  query GetCheckpoints {
    adminCheckpoints {
      processorId
      lastPosition
      updatedAt
    }
  }
`;

const GLOBAL_POSITION = gql`
  query GetGlobalPosition {
    adminGlobalPosition
  }
`;

const RESET_CHECKPOINT = gql`
  mutation ResetCheckpoint($processorId: String!) {
    adminResetCheckpoint(processorId: $processorId) {
      processorId
      success
    }
  }
`;

interface CheckpointRow {
  processorId: string;
  lastPosition: number;
  updatedAt: string | null;
}

export function Checkpoints() {
  const { data, loading, error } = useQuery(CHECKPOINTS, {
    pollInterval: 3000,
  });
  const { data: posData } = useQuery(GLOBAL_POSITION, { pollInterval: 5000 });
  const [resetCheckpoint] = useMutation(RESET_CHECKPOINT, {
    refetchQueries: [{ query: CHECKPOINTS }],
  });

  if (loading) return <div className="loading">Loading…</div>;
  if (error) return <div className="error-msg">{error.message}</div>;

  const checkpoints = ((data as any)?.adminCheckpoints ?? []) as CheckpointRow[];
  const globalPos = ((posData as any)?.adminGlobalPosition ?? 0) as number;

  return (
    <div className="card">
      <div className="card-header">
        <h2>Checkpoints</h2>
        <span className="badge">{checkpoints.length}</span>
      </div>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Processor</th>
              <th>Position</th>
              <th>Lag</th>
              <th>Updated</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {checkpoints.length === 0 ? (
              <tr>
                <td colSpan={5} className="empty">
                  No checkpoints
                </td>
              </tr>
            ) : (
              checkpoints.map((cp) => {
                const lag = globalPos - cp.lastPosition;
                return (
                  <tr key={cp.processorId}>
                    <td>
                      <code>{cp.processorId}</code>
                    </td>
                    <td>{cp.lastPosition.toLocaleString()}</td>
                    <td
                      style={{
                        color: lag > 100 ? "var(--warning)" : "inherit",
                      }}
                    >
                      {lag.toLocaleString()}
                    </td>
                    <td>{formatTime(cp.updatedAt)}</td>
                    <td>
                      <button
                        className="btn btn-danger btn-sm"
                        onClick={() => {
                          if (
                            confirm(
                              `Reset ${cp.processorId}? This triggers a full replay.`,
                            )
                          ) {
                            resetCheckpoint({
                              variables: { processorId: cp.processorId },
                            });
                          }
                        }}
                      >
                        Reset
                      </button>
                    </td>
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function formatTime(iso: string | null): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return d.toLocaleTimeString();
}
