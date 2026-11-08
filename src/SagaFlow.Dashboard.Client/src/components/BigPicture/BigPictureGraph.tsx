import { Background, BackgroundVariant, Controls, ReactFlow } from "@xyflow/react";
import { ArrowLeft, X } from "lucide-react";
import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import type { DefinitionGraph, SagaDefinition, InstanceRelationship, InstanceContext } from "../../api/sagaflowApi";
import type { WorkflowIdentity } from "../../layout/bigPictureRoute";
import { useTheme } from "../../theme/ThemeProvider";
import { mapBigPicture, processKey, relationshipMatches, type ProcessEdge, type ProcessNode, type ProcessNodeData, type ProcessEdgeData } from "./bigPictureMapping";
import { RuntimeRelationshipDetails } from "./RuntimeRelationshipDetails";
import { CopyIdentity } from "../InstanceTraceability";
import { ProcessNodeCard } from "./ProcessNodeCard";
import { ProcessRelationshipEdge } from "./ProcessRelationshipEdge";
import "@xyflow/react/dist/style.css";
import "../Workflow/workflow.css";
import "./bigpicture.css";

type Selection = { kind: "node"; id: string; domain: string; process: string; version: string | null }
  | { kind: "edge"; id: string } | { kind: "runtime"; id: string };
type SelectedData = { kind: "node"; data: ProcessNodeData } | { kind: "edge"; data: ProcessEdgeData }
  | { kind: "runtime"; data: InstanceRelationship } | { kind: "unloaded"; data: { invocationId: string; perspective: string } };
const nodeTypes = { process: ProcessNodeCard };
const edgeTypes = { relationship: ProcessRelationshipEdge };
const WorkflowGraph = lazy(() => import("../Workflow/WorkflowGraph"));

const noRuntime: InstanceRelationship[] = [];
export default function BigPictureGraph({ graph, definitions = [], workflow = null, onWorkflowOpen, onWorkflowBack,
  runtime = noRuntime, partial = false, instanceContext = null, onLocalInstanceOpen }: {
  graph: DefinitionGraph; definitions?: SagaDefinition[]; workflow?: WorkflowIdentity | null;
  onWorkflowOpen?: (workflow: WorkflowIdentity) => void; onWorkflowBack?: () => void;
  runtime?: InstanceRelationship[]; partial?: boolean; instanceContext?: InstanceContext | null;
  onLocalInstanceOpen?: (id: string) => void;
}) {
  const { theme } = useTheme();
  const mapped = useMemo(() => mapBigPicture(graph, definitions, runtime, partial), [graph, definitions, runtime, partial]);
  const [selection, setSelection] = useState<Selection | null>(null);
  const interactiveEdges = useMemo(() => mapped.edges.map(edge => ({ ...edge,
    data: edge.data ? { ...edge.data, onInspect: () => setSelection({ kind: "edge", id: edge.id }) } : undefined })), [mapped]);
  const interactiveNodes = useMemo(() => mapped.nodes.map(node => {
    const { domain, process, version } = node.data;
    return { ...node, data: { ...node.data,
      onOpenWorkflow: onWorkflowOpen ? () => { setSelection(null); onWorkflowOpen({ domain, process, version }); } : undefined,
      onInspect: () => setSelection({ kind: "node", id: node.id, domain, process, version }),
    } };
  }), [mapped.nodes, onWorkflowOpen]);
  const workflowDefinition = workflow ? definitions.find(definition => definition.domain === workflow.domain
    && definition.name === workflow.process && definition.version === workflow.version)
    ?? graph.nodes.find(node => node.domain === workflow.domain && node.process === workflow.process
      && node.version === workflow.version)?.definition : null;
  const selectedData = useMemo(() => {
    if (!selection) return null;
    if (selection.kind === "runtime") {
      const data = runtime.find(row => `${row.perspective}:${row.invocationId}` === selection.id);
      return data ? { kind: "runtime" as const, data } : { kind: "unloaded" as const,
        data: { invocationId: selection.id.slice(selection.id.indexOf(":") + 1), perspective: selection.id.split(":")[0] } };
    }
    if (selection.kind === "edge") {
      const data = mapped.edges.find(edge => edge.id === selection.id)?.data;
      return data ? { kind: "edge" as const, data } : null;
    }
    const data = (mapped.nodes.find(node => node.id === selection.id)
      ?? mapped.nodes.find(node => node.data.domain === selection.domain && node.data.process === selection.process
        && node.data.version === selection.version))?.data;
    return data ? { kind: "node" as const, data } : null;
  }, [mapped, selection, runtime]);
  const close = useCallback(() => setSelection(null), []);
  const remoteCount = mapped.nodes.filter(node => node.data.origin === "Remote").length;
  const localCount = mapped.nodes.length - remoteCount;
  const height = Math.min(720, Math.max(360, Math.max(remoteCount, localCount) * 154 + 60));

  if (workflow) return <section aria-label="Process Explorer workflow" className="flex h-[calc(100vh-230px)] min-h-[560px] flex-col gap-4">
    <div className="flex flex-wrap items-center justify-between gap-3">
      <button type="button" onClick={onWorkflowBack}
        className="inline-flex items-center gap-2 rounded-lg px-2 py-1.5 text-sm text-zinc-600 hover:bg-zinc-100 dark:text-zinc-300 dark:hover:bg-zinc-900">
        <ArrowLeft size={16} />Back to Topology
      </button>
      <span className="text-sm text-zinc-500">{workflow.domain} / {workflow.process} · {workflow.version ? `v${workflow.version}` : "Version unspecified"}</span>
    </div>
    {workflowDefinition ? <Suspense fallback={<div role="status">Loading workflow diagram…</div>}>
      <WorkflowGraph key={JSON.stringify([workflow.domain, workflow.process, workflow.version])} definition={workflowDefinition} />
    </Suspense> : <div role="status" className="rounded-xl border border-amber-500/40 p-5 text-sm">
      Workflow definition unavailable. Check the domain, process, and version, or wait for discovery to recover.
    </div>}
  </section>;

  return <section aria-label="Process Explorer topology" className="min-w-0">
    <div data-testid="bigpicture-canvas" className="bigpicture-canvas workflow-canvas relative overflow-hidden rounded-2xl border border-zinc-200 bg-zinc-50 dark:border-zinc-800 dark:bg-zinc-950"
      style={{ height }}>
      <ReactFlow<ProcessNode, ProcessEdge> nodes={interactiveNodes} edges={interactiveEdges} nodeTypes={nodeTypes} edgeTypes={edgeTypes}
        colorMode={theme} fitView fitViewOptions={{ padding: 0.18, minZoom: 0.6, maxZoom: 1 }}
        nodesDraggable={false} nodesFocusable={false} nodesConnectable={false} edgesReconnectable={false} deleteKeyCode={null}
        minZoom={0.25} maxZoom={1.6} onPaneClick={close}
        onEdgeClick={(_, edge) => { if (edge.data) setSelection({ kind: "edge", id: edge.id }); }}
        aria-label="BigPicture definition graph">
        <Background variant={BackgroundVariant.Dots} gap={22} size={1.5}
          color={theme === "dark" ? "#52525b" : "#a1a1aa"} bgColor={theme === "dark" ? "#09090b" : "#fafafa"} />
        <Controls showInteractive={false} />
      </ReactFlow>
    </div>
    {instanceContext && <section aria-label="Loaded runtime relationships" className="mt-4 space-y-2 text-sm">
      <h3 className="font-semibold">Selected instance relationships · {runtime.length}{partial ? "+" : ""} loaded</h3>
      {runtime.map(row => <button type="button" key={`${row.perspective}:${row.invocationId}`}
        className="block w-full break-all rounded-lg border border-zinc-200 px-3 py-2 text-left hover:bg-violet-50 dark:border-zinc-800 dark:hover:bg-violet-950/30"
        onClick={() => setSelection({ kind: "runtime", id: `${row.perspective}:${row.invocationId}` })}>
        {row.perspective} · {row.invocationId.slice(0, 8)} · {row.source.process} → {row.target.process}
        {!graph.edges.some(edge => relationshipMatches(edge, row)) && <span className="ml-2 text-amber-700 dark:text-amber-300">No matching declared invokes edge</span>}
      </button>)}
    </section>}
    <BigPictureDrawer selection={selectedData} onClose={close} runtime={runtime} instanceContext={instanceContext}
      onWorkflowOpen={onWorkflowOpen} onLocalInstanceOpen={onLocalInstanceOpen} />
  </section>;
}

function BigPictureDrawer({ selection, onClose, runtime, instanceContext, onWorkflowOpen, onLocalInstanceOpen }: {
  selection: SelectedData | null; onClose: () => void; runtime: InstanceRelationship[]; instanceContext: InstanceContext | null;
  onWorkflowOpen?: (workflow: WorkflowIdentity) => void; onLocalInstanceOpen?: (id: string) => void;
}) {
  const drawer = useRef<HTMLElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const selectionKey = selection?.kind === "runtime" || selection?.kind === "unloaded" ? `${selection.data.perspective}:${selection.data.invocationId}`
    : selection?.kind === "node" ? `node:${selection.data.domain}/${selection.data.process}@${selection.data.version}`
      : selection ? `edge:${selection.data.source}:${selection.data.target}:${selection.data.relationship}` : null;
  useEffect(() => {
    if (!selectionKey) return;
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const panel = drawer.current;
    closeButton.current?.focus({ preventScroll: true });
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") { event.preventDefault(); onClose(); }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
      if (previousFocus?.isConnected && (document.activeElement === document.body || panel?.contains(document.activeElement)))
        previousFocus.focus({ preventScroll: true });
    };
  }, [selectionKey, onClose]);
  if (!selection) return null;
  const node = selection.kind === "node" ? selection.data : null;
  const edge = selection.kind === "edge" ? selection.data : null;
  const related = selection.kind === "runtime" ? [selection.data] : edge ? edge.runtime ?? [] : node
    ? runtime.filter(row => [processKey(row.source), processKey(row.target)].includes(`${node.domain}/${node.process}@${node.version}`)) : [];
  return createPortal(<aside ref={drawer} role="dialog" aria-modal={false} aria-label="BigPicture details"
    className="workflow-details-drawer fixed inset-y-0 right-0 z-[70] flex w-[min(400px,100vw)] flex-col border-l border-zinc-200 bg-white shadow-2xl dark:border-zinc-800 dark:bg-zinc-950">
    <header className="flex items-start justify-between gap-3 border-b border-zinc-200 px-5 py-4 dark:border-zinc-800">
      <div className="min-w-0"><h2 className="text-sm font-semibold text-zinc-950 dark:text-zinc-50">{node ? "Process definition" : edge ? "Declared reference" : "Runtime invocation"}</h2>
        <p className="mt-1 text-xs text-zinc-500">{node ? `${node.domain} / ${node.process}` : edge ? edge.relationship ?? "Legacy reference" : "Runtime relationship"}</p></div>
      <button ref={closeButton} type="button" onClick={onClose} aria-label="Close BigPicture details" title="Close (Escape)"
        className="rounded-lg p-1.5 text-zinc-500 hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-emerald-500 dark:hover:bg-zinc-800"><X size={16} /></button>
    </header>
    <div className="min-h-0 overflow-y-auto">
    {instanceContext && <p className="break-all border-b border-zinc-200 p-5 text-xs dark:border-zinc-800">Selected local instance: {instanceContext.instanceId}<br />
      {processKey(instanceContext)}</p>}
    <dl className="space-y-4 p-5 text-sm">
      {node ? <>
        <Detail label="Domain" value={node.domain} /><Detail label="Process" value={node.process} />
        <Detail label="Version" value={node.version ?? "Unspecified"} />
        <Detail label="Ownership" value={node.origin} /><Detail label="Discovery status" value={node.status} />
        {node.steps ? <Detail label="YAML steps" value={node.steps.join(", ") || "None"} /> : null}
        {node.failure ? <Detail label="Discovery reason" value={node.failure} /> : null}
        {node.status === "Resolved" && node.version && onWorkflowOpen && <button type="button" className="rounded-lg border px-3 py-2"
          onClick={() => { onClose(); onWorkflowOpen({ domain: node.domain, process: node.process, version: node.version! }); }}>Open workflow definition</button>}
      </> : edge ? <>
        <Detail label="Source process" value={edge.source} /><Detail label="Target process" value={edge.target} />
        <Detail label="Declared relationship" value={edge.relationship ?? "None (legacy reference)"} />
        <Detail label="Direction of declaration" value="Source → target" />
        <Detail label="Resolution status" value={edge.status} />
        <Detail label="Source ownership" value={edge.sourceOrigin ?? "Unknown"} /><Detail label="Target ownership" value={edge.targetOrigin ?? "Unknown"} />
        <p className="text-xs text-zinc-500">Static declaration only. An invokes reference does not prove an invocation occurred.</p>
        {edge.failure ? <Detail label="Discovery reason" value={edge.failure} /> : null}
      </> : null}
    </dl>
    {related.map(row => <RuntimeRelationshipDetails key={`${row.perspective}:${row.invocationId}`} row={row} onLocalInstanceOpen={onLocalInstanceOpen} />)}
    {selection.kind === "unloaded" && <section className="space-y-3 p-5 text-sm" aria-label="Unloaded selected invocation">
      <CopyIdentity label="Invocation ID" value={selection.data.invocationId} />
      <p>This selected invocation is not in the currently loaded results. Load more relationships or refresh to locate it. Previous status is not displayed.</p>
    </section>}
    {instanceContext && related.length === 0 && <p className="p-5 text-sm text-zinc-500">No loaded runtime records for this selection.</p>}
    </div>
  </aside>, document.body);
}

function Detail({ label, value }: { label: string; value: string }) {
  return <div><dt className="text-xs font-medium uppercase tracking-wide text-zinc-500 dark:text-zinc-400">{label}</dt>
    <dd className="mt-1 break-words font-medium text-zinc-900 dark:text-zinc-100">{value}</dd></div>;
}
