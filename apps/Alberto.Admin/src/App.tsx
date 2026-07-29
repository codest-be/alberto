import { SystemOverview } from "./components/SystemOverview";
import { Checkpoints } from "./components/Checkpoints";
import { DeadLetters } from "./components/DeadLetters";
import { AuditLog } from "./components/AuditLog";

function App() {
  return (
    <div className="app">
      <header className="app-header">
        <span className="logo">⚡</span>
        <h1>Alberto Admin</h1>
      </header>
      <main className="app-body">
        <SystemOverview />
        <Checkpoints />
        <DeadLetters />
        <AuditLog />
      </main>
    </div>
  );
}

export default App;
