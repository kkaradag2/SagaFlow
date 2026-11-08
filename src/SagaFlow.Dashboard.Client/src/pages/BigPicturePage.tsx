import { Network, RefreshCw } from "lucide-react";
import { lazy, Suspense, useEffect, useMemo, useRef, useState } from "react";
import { getDefinitionGraph, type DefinitionGraph, type SagaDefinition, type InstanceContext } from "../api/sagaflowApi";
import { useInstanceRelationships } from "../live/useInstanceRelationships";
import { EmptyPanel } from "../layout/DashboardLayout";
import type { WorkflowIdentity } from "../layout/bigPictureRoute";

const BigPictureGraph = lazy(() => import("../components/BigPicture/BigPictureGraph"));

const definitionKey = (domain: string, process: string, version: string) =>
  JSON.stringify([domain, process, version]);

export function aggregateDefinitionGraphs(graphs: DefinitionGraph[], localDefinitions: SagaDefinition[]): DefinitionGraph {
  const nodes = new Map<string, DefinitionGraph["nodes"][number]>();
  for (const definition of localDefinitions) {
    const { domain, name: process, version } = definition;
    nodes.set(definitionKey(domain, process, version), { domain, process, version, origin: "Local" });
  }
  for (const node of graphs.flatMap(graph => graph.nodes)) {
    const key = definitionKey(node.domain, node.process, node.version);
    if (nodes.get(key)?.origin !== "Local") nodes.set(key, { ...node, origin: "Remote" });
  }
  return { nodes: [...nodes.values()], edges: graphs.flatMap(graph => graph.edges),
    failures: graphs.flatMap(graph => graph.failures) };
}

export function BigPicturePage({ definitions, refreshIntervalMs = 15000, workflow = null, onWorkflowOpen, onWorkflowBack,
  instanceContext = null, onContextClear, onLocalInstanceOpen }: {
  definitions: SagaDefinition[]; refreshIntervalMs?: number; workflow?: WorkflowIdentity | null;
  onWorkflowOpen?: (workflow: WorkflowIdentity) => void; onWorkflowBack?: () => void;
  instanceContext?: InstanceContext | null; onContextClear?: () => void; onLocalInstanceOpen?: (id: string) => void;
}) {
  const runtime = useInstanceRelationships(instanceContext);
  const [graph, setGraph] = useState<DefinitionGraph | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const refresh = useRef<() => void>(() => {});
  const roots = useMemo(() => definitions.filter(definition =>
    definition.bigPicture.some(reference => reference.relationship === "related" || reference.relationship === "invokes")
    || (definition.domain === instanceContext?.domain && definition.name === instanceContext.process && definition.version === instanceContext.version)),
    [definitions, instanceContext?.domain, instanceContext?.process, instanceContext?.version]);
  const identities = useMemo(() => roots.map(d => definitionKey(d.domain, d.name, d.version)).join("|"), [roots]);

  useEffect(() => {
    let disposed = false;
    let inFlight = false;
    let queued = false;
    let timer: number | undefined;
    let activeRequest: AbortController | undefined;
    const lastSuccessfulGraphs = new Map<SagaDefinition, DefinitionGraph>();
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
        const results = await Promise.allSettled(roots.map(async definition => getDefinitionGraph(definition, abort.signal)));
        if (disposed || abort.signal.aborted) return;
        const failures: string[] = [];
        const graphs = results.flatMap((result, index) => {
          const root = roots[index];
          if (result.status === "fulfilled") {
            lastSuccessfulGraphs.set(root, result.value);
            return [result.value];
          }
          const denied = result.reason instanceof Error && /\((401|403)\)/.test(result.reason.message);
          if (denied) lastSuccessfulGraphs.delete(root);
          const message = denied ? "Access to process relationships was denied" : "Unable to load or refresh process relationships";
          failures.push(`${message} for ${root.domain}/${root.name}@${root.version}.`);
          const previous = lastSuccessfulGraphs.get(root);
          return previous ? [previous] : [];
        });
        const next = aggregateDefinitionGraphs(graphs, definitions);
        setGraph(previous => previous && JSON.stringify(previous) === JSON.stringify(next) ? previous : next);
        setError(failures.length ? failures.join(" ") : null);
      } catch {
        if (disposed || abort.signal.aborted) return;
        lastSuccessfulGraphs.clear();
        setGraph(aggregateDefinitionGraphs([], definitions));
        setError("Unable to load or refresh process relationships.");
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

  const showGraph = Boolean(graph?.nodes.length || instanceContext || workflow);

  return <section className="mx-auto flex min-h-full w-full max-w-[1280px] flex-col gap-5" aria-label="Process Explorer page">
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div><p className="text-sm text-zinc-600 dark:text-zinc-400">Process relationships across Domain Starters</p>
        <p className="mt-1 text-xs text-zinc-500 dark:text-zinc-400">Declared definition relationships. Arrows show the direction of declaration. Invokes is a static declaration, not proof of execution.</p>
        <p className="mt-1 text-xs text-zinc-500">Dashed: related / unresolved. Violet: invokes. Green: loaded runtime records for the selected instance.</p></div>
      <button type="button" disabled={loading || runtime.loading} onClick={() => { refresh.current(); runtime.refresh(); }}
        className="inline-flex items-center gap-2 rounded-xl border border-zinc-200 bg-white px-3 py-2 text-sm text-zinc-700 hover:bg-zinc-50 disabled:opacity-50 dark:border-zinc-800 dark:bg-zinc-950 dark:text-zinc-200 dark:hover:bg-zinc-900">
        <RefreshCw size={15} className={loading ? "animate-spin" : ""} />Refresh</button>
    </div>
    {instanceContext ? <section aria-label="Instance context" className="min-w-0 space-y-2 rounded-xl border border-violet-200 p-4 text-sm dark:border-violet-900">
      <h2 className="font-semibold">Instance Context</h2>
      <p className="break-all font-mono text-xs">{instanceContext.domain}/{instanceContext.process}@{instanceContext.version} · {instanceContext.instanceId}</p>
      <p className="text-xs text-zinc-500">Only this local instance’s observations. Counts are not system-wide totals; remote state is not queried.</p>
      {onContextClear && <button type="button" className="underline" onClick={onContextClear}>Clear instance context</button>}
      {runtime.loading && <p role="status">Loading instance relationships…</p>}
      {runtime.error && <div role="alert">Unable to load instance relationships: {runtime.error} <button type="button" className="underline" onClick={runtime.refresh}>Retry instance relationships</button></div>}
      {!runtime.loading && !runtime.error && runtime.items.length === 0 && <p>No runtime relationships recorded for this local instance.</p>}
      {runtime.nextOffset !== null && <div><p>Partial results: {runtime.items.length} loaded; more records are available.</p>
        <button type="button" disabled={runtime.loading} className="mt-2 rounded-lg border px-3 py-2" onClick={runtime.loadMore}>Load more relationships</button></div>}
    </section> : <p className="text-xs text-zinc-500">Definition View. Open a local instance in Process Explorer to inspect runtime observations.</p>}
    {error && <div role="alert" className="rounded-xl border border-red-500/40 p-5 text-sm text-red-600 dark:text-red-400">{error}</div>}
    {loading ? <div role="status" className="rounded-xl border border-zinc-200 p-6 text-sm text-zinc-500 dark:border-zinc-800">Loading process relationships…</div>
      : showGraph ?
        <Suspense fallback={<div role="status" className="text-sm text-zinc-500">Preparing graph…</div>}>
          <BigPictureGraph key={instanceContext ? JSON.stringify([instanceContext.instanceId, instanceContext.domain, instanceContext.process, instanceContext.version]) : "definition"}
            graph={graph ?? { nodes: [], edges: [], failures: [] }} definitions={definitions} workflow={workflow}
            onWorkflowOpen={onWorkflowOpen} onWorkflowBack={onWorkflowBack} instanceContext={instanceContext}
            runtime={runtime.items} partial={runtime.nextOffset !== null} onLocalInstanceOpen={onLocalInstanceOpen} />
        </Suspense> : !error && <EmptyPanel icon={<Network size={20} />} title="No process definitions available."
          description="Local workflows appear here after the host loads their definitions." />}
  </section>;
}
