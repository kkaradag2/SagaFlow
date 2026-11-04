import { Network, RefreshCw } from "lucide-react";
import { lazy, Suspense, useEffect, useMemo, useRef, useState } from "react";
import { getDefinitionGraph, type DefinitionGraph, type SagaDefinition } from "../api/sagaflowApi";
import { EmptyPanel } from "../layout/DashboardLayout";
import type { WorkflowIdentity } from "../layout/bigPictureRoute";

const BigPictureGraph = lazy(() => import("../components/BigPicture/BigPictureGraph"));

const definitionKey = (domain: string, process: string, version: string) =>
  JSON.stringify([domain, process, version]);

export function aggregateDefinitionGraphs(graphs: DefinitionGraph[], localDefinitions: SagaDefinition[]): DefinitionGraph {
  const localIds = new Set(localDefinitions.map(definition =>
    definitionKey(definition.domain, definition.name, definition.version)));
  const nodes = new Map<string, DefinitionGraph["nodes"][number]>();
  for (const node of graphs.flatMap(graph => graph.nodes)) {
    const key = definitionKey(node.domain, node.process, node.version);
    nodes.set(key, { ...node, origin: localIds.has(key) ? "Local" : "Remote" });
  }
  return { nodes: [...nodes.values()], edges: graphs.flatMap(graph => graph.edges),
    failures: graphs.flatMap(graph => graph.failures) };
}

export function BigPicturePage({ definitions, refreshIntervalMs = 15000, workflow = null, onWorkflowOpen, onWorkflowBack }: {
  definitions: SagaDefinition[]; refreshIntervalMs?: number; workflow?: WorkflowIdentity | null;
  onWorkflowOpen?: (workflow: WorkflowIdentity) => void; onWorkflowBack?: () => void;
}) {
  const [graph, setGraph] = useState<DefinitionGraph | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const refresh = useRef<() => void>(() => {});
  const roots = useMemo(() => definitions.filter(definition =>
    definition.bigPicture.some(reference => reference.relationship === "related")), [definitions]);
  const identities = useMemo(() => roots.map(d => definitionKey(d.domain, d.name, d.version)).join("|"), [roots]);

  useEffect(() => {
    let disposed = false;
    let inFlight = false;
    let queued = false;
    let timer: number | undefined;
    let activeRequest: AbortController | undefined;
    setGraph(null);
    setError(null);
    setLoading(true);
    const schedule = () => {
      if (!disposed && refreshIntervalMs > 0)
        timer = window.setTimeout(() => { if (document.hidden) schedule(); else void run(); }, refreshIntervalMs);
    };
    const run = async () => {
      if (disposed) return;
      if (inFlight) { queued = true; return; }
      if (timer !== undefined) window.clearTimeout(timer);
      inFlight = true;
      const abort = new AbortController();
      activeRequest = abort;
      try {
        const graphs = await Promise.all(roots.map(definition => getDefinitionGraph(definition, abort.signal)));
        if (disposed || abort.signal.aborted) return;
        const next = aggregateDefinitionGraphs(graphs, definitions);
        setGraph(previous => previous && JSON.stringify(previous) === JSON.stringify(next) ? previous : next);
        setError(null);
      } catch (failure) {
        if (disposed || abort.signal.aborted) return;
        const denied = failure instanceof Error && /\((401|403)\)/.test(failure.message);
        if (denied) setGraph(null);
        setError(denied ? "Access to process relationships was denied." : "Unable to load or refresh process relationships. The last available graph remains visible when possible.");
      } finally {
        inFlight = false;
        activeRequest = undefined;
        if (!disposed) {
          setLoading(false);
          if (queued) { queued = false; void run(); }
          else schedule();
        }
      }
    };
    refresh.current = () => { void run(); };
    const onVisible = () => { if (!document.hidden) void run(); };
    document.addEventListener("visibilitychange", onVisible);
    void run();
    return () => {
      disposed = true;
      refresh.current = () => {};
      if (timer !== undefined) window.clearTimeout(timer);
      activeRequest?.abort();
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [identities, definitions, roots, refreshIntervalMs]);

  return <section className="mx-auto flex min-h-full w-full max-w-[1280px] flex-col gap-5" aria-label="BigPicture page">
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div><p className="text-sm text-zinc-600 dark:text-zinc-400">Process relationships across Domain Starters</p>
        <p className="mt-1 text-xs text-zinc-500 dark:text-zinc-400">Declared definition relationships. Arrows show the direction of declaration.</p></div>
      <button type="button" disabled={loading} onClick={() => refresh.current()}
        className="inline-flex items-center gap-2 rounded-xl border border-zinc-200 bg-white px-3 py-2 text-sm text-zinc-700 hover:bg-zinc-50 disabled:opacity-50 dark:border-zinc-800 dark:bg-zinc-950 dark:text-zinc-200 dark:hover:bg-zinc-900">
        <RefreshCw size={15} className={loading ? "animate-spin" : ""} />Refresh</button>
    </div>
    {error && <div role="alert" className="rounded-xl border border-red-500/40 p-5 text-sm text-red-600 dark:text-red-400">{error}</div>}
    {loading ? <div role="status" className="rounded-xl border border-zinc-200 p-6 text-sm text-zinc-500 dark:border-zinc-800">Loading process relationships…</div>
      : graph && graph.edges.some(edge => edge.relationship === "related") ?
        <Suspense fallback={<div role="status" className="text-sm text-zinc-500">Preparing graph…</div>}>
          <BigPictureGraph graph={graph} definitions={definitions} workflow={workflow}
            onWorkflowOpen={onWorkflowOpen} onWorkflowBack={onWorkflowBack} />
        </Suspense> : !error && <EmptyPanel icon={<Network size={20} />} title="No process relationships available."
          description="This Dashboard has no visible declared relationships." />}
  </section>;
}
