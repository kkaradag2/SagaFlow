import { Background, BackgroundVariant, Controls, ReactFlow, useNodesState } from "@xyflow/react";
import ELK from "elkjs/lib/elk-api";
import elkWorkerUrl from "elkjs/lib/elk-worker.min.js?url";
import { useCallback, useEffect, useMemo, useState } from "react";
import type { SagaDefinition } from "../../api/sagaflowApi";
import { useTheme } from "../../theme/ThemeProvider";
import { createWorkflowGraph, type WorkflowEdge, type WorkflowNode } from "./workflowMapping";
import { layoutWorkflow } from "./workflowLayout";
import { WorkflowNodeCard } from "./WorkflowNodeCard";
import { WorkflowDetailsDrawer } from "./WorkflowDetailsDrawer";
import "@xyflow/react/dist/style.css";
import "./workflow.css";

const nodeTypes = { workflow: WorkflowNodeCard };

export default function WorkflowGraph({ definition }: { definition: SagaDefinition }) {
  const { theme } = useTheme();
  const [nodes, setNodes, onNodesChange] = useNodesState<WorkflowNode>([]);
  const [edges, setEdges] = useState<WorkflowEdge[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showFailurePaths, setShowFailurePaths] = useState(false);
  const selected = useMemo(() => nodes.find(node => node.id === selectedId)?.data ?? null, [nodes, selectedId]);

  useEffect(() => {
    let active = true;
    const elk = new ELK({ workerUrl: elkWorkerUrl });
    setLoading(true);
    setError(null);
    setSelectedId(null);
    void (async () => {
      try {
        const graph = await layoutWorkflow(createWorkflowGraph(definition, { showFailurePaths }), elk);
        if (active) { setNodes(graph.nodes); setEdges(graph.edges); }
      } catch (failure) {
        if (active) setError(failure instanceof Error ? failure.message : "Unable to lay out the workflow.");
      } finally { if (active) setLoading(false); }
    })();
    return () => { active = false; elk.terminateWorker(); };
  }, [definition, showFailurePaths, setNodes]);

  const selectNode = useCallback((_event: unknown, node: WorkflowNode) => setSelectedId(node.id), []);
  const selectNodes = useCallback(({ nodes: selection }: { nodes: WorkflowNode[] }) => setSelectedId(selection[0]?.id ?? null), []);
  const closeDrawer = useCallback(() => {
    setSelectedId(null);
    setNodes(current => current.map(node => node.selected ? { ...node, selected: false } : node));
  }, [setNodes]);

  return <div className="flex min-h-[420px] min-w-0 flex-1 flex-col gap-3">
    <div className="flex shrink-0 justify-end text-xs text-zinc-500">
      <label className="inline-flex cursor-pointer items-center gap-2 text-zinc-700 dark:text-zinc-300">
        <input type="checkbox" checked={showFailurePaths} disabled={loading} onChange={event => setShowFailurePaths(event.target.checked)} className="size-4 accent-emerald-500" />Show Failure Paths
      </label>
    </div>
    <div data-testid="workflow-canvas" className="workflow-canvas relative min-h-[360px] flex-1 overflow-hidden rounded-xl border border-zinc-200 dark:border-zinc-800">
      {error ? <div role="alert" className="p-5 text-sm text-red-500">{error}</div> : loading ?
        <div role="status" className="flex h-full min-h-[360px] items-center justify-center text-sm text-zinc-500">Laying out workflow…</div> :
        <ReactFlow<WorkflowNode, WorkflowEdge> nodes={nodes} edges={edges} nodeTypes={nodeTypes} onNodesChange={onNodesChange}
          onNodeClick={selectNode} onPaneClick={closeDrawer}
          onSelectionChange={selectNodes}
          nodesDraggable={false} nodesConnectable={false} edgesReconnectable={false} deleteKeyCode={null}
          selectionOnDrag={false} multiSelectionKeyCode={null} colorMode={theme} minZoom={0.1} maxZoom={2}
          autoPanOnNodeFocus={false} fitView fitViewOptions={{ padding: 0.12, minZoom: 0.9, maxZoom: 0.9 }} aria-label={`${definition.name} workflow diagram`}>
          <Background variant={BackgroundVariant.Dots} gap={22} size={1.5} color={theme === "dark" ? "#52525b" : "#94a3b8"} bgColor={theme === "dark" ? "#09090b" : "#fafafa"} />
          <Controls showInteractive={false} />
        </ReactFlow>}
      <WorkflowDetailsDrawer node={selected} onClose={closeDrawer} />
    </div>
  </div>;
}
