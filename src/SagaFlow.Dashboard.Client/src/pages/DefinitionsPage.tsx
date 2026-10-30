import { ArrowLeft, ChevronRight, GitBranch, RefreshCw } from "lucide-react";
import { lazy, Suspense, useEffect, useState } from "react";

import { EmptyPanel, PageSection } from "../layout/DashboardLayout";
import { getDefinitions, type SagaDefinition } from "../api/sagaflowApi";

const WorkflowGraph = lazy(() => import("../components/Workflow/WorkflowGraph"));

export function DefinitionsPage({ onWorkflowOpen }: { onWorkflowOpen: () => void }) {
  const [definitions, setDefinitions] = useState<SagaDefinition[]>([]);
  const [selected, setSelected] = useState<SagaDefinition | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    const abort = new AbortController();
    setLoading(true);
    setError(null);
    void getDefinitions(abort.signal).then(result => {
      if (!abort.signal.aborted) setDefinitions(result);
    }).catch(failure => {
      if (!abort.signal.aborted) setError(failure instanceof Error ? failure.message : "Unable to load workflows.");
    }).finally(() => { if (!abort.signal.aborted) setLoading(false); });
    return () => abort.abort();
  }, [reload]);

  if (selected) return <div className="flex h-full min-h-[560px] flex-col gap-4">
    <div className="flex shrink-0 flex-wrap items-center justify-between gap-3">
      <nav aria-label="Workflow breadcrumb" className="flex min-w-0 items-center gap-2 text-sm">
        <button type="button" onClick={() => setSelected(null)} className="inline-flex items-center gap-2 rounded-lg px-2 py-1.5 text-zinc-500 hover:bg-zinc-100 hover:text-zinc-950 dark:hover:bg-zinc-900 dark:hover:text-white"><ArrowLeft size={15} />Workflows</button>
        <ChevronRight size={14} className="shrink-0 text-zinc-400" />
        <span aria-current="page" className="truncate font-semibold">{selected.name}</span>
      </nav>
      <span className="text-xs text-zinc-500">{selected.domain} · v{selected.version} · {selected.steps.length} YAML steps</span>
    </div>
    <Suspense fallback={<div role="status" className="p-6 text-sm text-zinc-500">Loading workflow diagram…</div>}>
      <WorkflowGraph key={JSON.stringify([selected.domain, selected.name, selected.version])} definition={selected} />
    </Suspense>
    {selected.bigPicture.length > 0 ? <details className="rounded-xl border border-zinc-200 p-5 dark:border-zinc-800">
      <summary className="cursor-pointer text-sm font-medium">Declared workflow references ({selected.bigPicture.length})</summary>
      <p className="mt-3 text-xs text-zinc-500">Reference metadata only. No remote workflow is loaded.</p>
      <ul className="mt-4 space-y-3">{selected.bigPicture.map((reference, index) => <li key={index} className="break-words text-sm">
        <span className="font-medium">{reference.domain} / {reference.process}</span>{reference.version ? <span className="text-zinc-500"> · v{reference.version}</span> : null}
        <div className="mt-1 font-mono text-xs text-zinc-500">{reference.endpoint}</div>
      </li>)}</ul>
    </details> : null}
  </div>;

  return <PageSection>
    <div className="flex flex-wrap items-center justify-between gap-4">
      <div><h2 className="flex items-center gap-2 text-lg font-semibold"><GitBranch size={18} className="text-emerald-500" />Local workflows</h2>
        <p className="mt-1 text-sm text-zinc-500">Read-only process definitions loaded by this Domain Starter.</p>
      </div>
      <button type="button" disabled={loading} onClick={() => setReload(value => value + 1)} className="inline-flex items-center gap-2 rounded-xl border border-zinc-200 bg-white px-4 py-2 text-sm hover:bg-zinc-50 disabled:opacity-50 dark:border-zinc-800 dark:bg-zinc-950 dark:hover:bg-zinc-900"><RefreshCw size={15} className={loading ? "animate-spin" : ""} />Refresh workflows</button>
    </div>
    {error ? <div role="alert" className="rounded-xl border border-red-500/40 p-4 text-sm text-red-500">{error}</div> : null}
    {loading ? <div role="status" className="p-5 text-sm text-zinc-500">Loading workflows…</div> : definitions.length === 0 ? <EmptyPanel icon={<GitBranch size={20} />} title="No workflows loaded." description="Local YAML definitions will appear here after the host loads them." /> :
      <div aria-label="Workflow list" className="overflow-hidden rounded-2xl border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950/40">
        <div className="hidden grid-cols-[minmax(0,1fr)_150px_100px_90px_20px] gap-4 border-b border-zinc-200 bg-zinc-50 px-5 py-3 text-xs font-medium uppercase tracking-wider text-zinc-500 dark:border-zinc-800 dark:bg-zinc-900/50 md:grid"><span>Workflow</span><span>Domain</span><span>Version</span><span>Steps</span><span /></div>
        <div className="divide-y divide-zinc-100 dark:divide-zinc-900">{definitions.map(definition => <button type="button" key={JSON.stringify([definition.domain, definition.name, definition.version])}
          onClick={() => { setSelected(definition); onWorkflowOpen(); }} className="grid w-full items-center gap-3 px-5 py-5 text-left text-sm transition hover:bg-zinc-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-inset focus-visible:outline-emerald-500 dark:hover:bg-zinc-900/50 md:grid-cols-[minmax(0,1fr)_150px_100px_90px_20px] md:gap-4">
          <span className="flex min-w-0 items-center gap-3"><GitBranch size={18} className="shrink-0 text-emerald-500" /><span className="truncate font-semibold">{definition.name}</span></span>
          <span className="text-zinc-500"><span className="mr-2 md:hidden">Domain:</span>{definition.domain}</span>
          <span className="text-zinc-500">v{definition.version}</span><span className="text-zinc-500">{definition.steps.length} steps</span><ChevronRight size={16} className="hidden text-zinc-400 md:block" />
        </button>)}</div>
      </div>}
  </PageSection>;
}
