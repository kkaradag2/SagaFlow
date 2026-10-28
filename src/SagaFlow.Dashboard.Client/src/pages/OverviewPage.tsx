import { Activity, RefreshCw } from "lucide-react";
import { useCallback, useEffect, useState } from "react";

import { getInstances, type SagaInstanceSummary } from "../api/sagaflowApi";
import { EmptyPanel, PageSection, SummaryPanel } from "../layout/DashboardLayout";

export function OverviewPage() {
  const [instances, setInstances] = useState<SagaInstanceSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try { setInstances(await getInstances()); }
    catch (requestError) { setError(requestError instanceof Error ? requestError.message : "Unable to load Saga instances."); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const running = instances.filter(instance => instance.status === "Running").length;
  const failed = instances.filter(instance => instance.status === "Failed").length;
  const completed = instances.filter(instance => instance.status === "Completed").length;

  return (
    <PageSection>
      <SummaryPanel
        icon={<Activity className="size-4" />}
        title="Local Saga Runtime"
        description="Live instance state from the host microservice's configured Saga state store."
        metric={`${instances.length} instances`}
      />
      <div className="grid gap-4 sm:grid-cols-3">
        <Metric label="Running" value={running} tone="text-amber-600 dark:text-amber-400" />
        <Metric label="Completed" value={completed} tone="text-emerald-600 dark:text-emerald-400" />
        <Metric label="Failed" value={failed} tone="text-red-600 dark:text-red-400" />
      </div>
      <div className="flex items-center justify-between gap-4"><h2 className="text-base font-bold">Recent instances</h2><button type="button" onClick={() => void refresh()} disabled={loading} className="inline-flex items-center gap-2 rounded-xl border border-zinc-200 bg-white px-4 py-2 text-sm font-medium hover:bg-zinc-50 disabled:opacity-50 dark:border-zinc-800 dark:bg-zinc-950 dark:hover:bg-zinc-900"><RefreshCw size={15} className={loading ? "animate-spin" : ""} /> Refresh</button></div>
      {error && <div role="alert" className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-700 dark:border-red-900 dark:bg-red-950/30 dark:text-red-300">{error}</div>}
      {instances.length === 0 && !loading ? <EmptyPanel icon={<Activity size={20} />} title="No saga instances." description="Started Saga instances will appear here." /> : <div className="overflow-x-auto rounded-2xl border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950/50"><table className="w-full min-w-[760px] text-left text-sm"><thead className="border-b border-zinc-200 bg-zinc-50 text-xs uppercase tracking-wide text-zinc-500 dark:border-zinc-800 dark:bg-zinc-900/60"><tr>{["Process", "Status", "Current Step", "Instance ID", "Updated"].map(label => <th key={label} className="px-4 py-3">{label}</th>)}</tr></thead><tbody className="divide-y divide-zinc-100 dark:divide-zinc-900">{instances.slice(0, 10).map(instance => <tr key={instance.id}><td className="px-4 py-3 font-medium">{instance.processName}</td><td className="px-4 py-3">{instance.status}</td><td className="px-4 py-3">{instance.currentStep ?? "—"}</td><td className="px-4 py-3 font-mono text-xs">{instance.id}</td><td className="px-4 py-3 whitespace-nowrap">{new Date(instance.updatedAt).toLocaleString()}</td></tr>)}</tbody></table></div>}
    </PageSection>
  );
}

function Metric({ label, value, tone }: { label: string; value: number; tone: string }) {
  return <div className="rounded-2xl border border-zinc-200 bg-white p-5 dark:border-zinc-800 dark:bg-zinc-950/50"><div className="text-xs font-medium uppercase tracking-wide text-zinc-500">{label}</div><div className={`mt-2 text-3xl font-bold ${tone}`}>{value}</div></div>;
}
