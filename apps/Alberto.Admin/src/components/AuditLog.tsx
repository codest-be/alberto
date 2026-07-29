import { gql } from "@apollo/client";
import { useSubscription } from "@apollo/client/react";
import { useCallback, useState } from "react";

// TODO: Replace with generated hooks from `npm run codegen`

const ON_AUDIT_EVENT = gql`
  subscription OnAuditEvent {
    onAdminAuditEvent {
      eventType
      operatorId
      description
      timestamp
    }
  }
`;

interface AuditEntry {
  eventType: string;
  operatorId: string;
  description: string;
  timestamp: string;
}

export function AuditLog() {
  const [entries, setEntries] = useState<AuditEntry[]>([]);

  const addEntry = useCallback((entry: AuditEntry) => {
    setEntries((prev) => [entry, ...prev].slice(0, 100));
  }, []);

  const { error } = useSubscription(ON_AUDIT_EVENT, {
    onData: ({ data: subData }) => {
      const entry = (subData as any).data?.onAdminAuditEvent as AuditEntry | undefined;
      if (entry) addEntry(entry);
    },
  });

  return (
    <div className="card" style={{ gridColumn: "1 / -1" }}>
      <div className="card-header">
        <h2>Audit Log</h2>
        <span className="badge">
          {entries.length > 0 ? `${entries.length} events` : "Listening…"}
        </span>
      </div>

      {error && <div className="error-msg">{error.message}</div>}

      {entries.length === 0 ? (
        <div className="empty">
          Waiting for admin operations… Perform a mutation to see events here.
        </div>
      ) : (
        <ul className="audit-list">
          {entries.map((e, i) => (
            <li key={`${e.timestamp}-${i}`} className="audit-item">
              <span className="audit-time">
                {new Date(e.timestamp).toLocaleTimeString()}
              </span>
              <span className="audit-type">{e.eventType}</span>
              <span className="audit-desc">{e.description}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
