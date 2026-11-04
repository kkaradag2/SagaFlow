import { MarkerType, Position, type Edge, type Node } from "@xyflow/react";
import type { DefinitionGraph, DefinitionIdentity, SagaDefinition } from "../../api/sagaflowApi";

export type ProcessNodeData = { domain: string; process: string; version: string | null;
  origin: "Local" | "Remote"; status: "Resolved" | "Unresolved"; failure?: string; steps?: string[] };
export type ProcessEdgeData = { source: string; target: string; relationship: string | null;
  status: "Resolved" | "Unresolved"; failure?: string };
export type ProcessNode = Node<ProcessNodeData, "process">;
export type ProcessEdge = Edge<ProcessEdgeData>;

const identity = (value: DefinitionIdentity) => `${value.domain}/${value.name}@${value.version}`;
const targetIdentity = (domain: string, process: string, version: string | null) =>
  `${domain}/${process}@${version ?? "?"}`;

export function mapBigPicture(graph: DefinitionGraph, definitions: SagaDefinition[] = []): { nodes: ProcessNode[]; edges: ProcessEdge[] } {
  const nodes = new Map<string, ProcessNode>();
  const local: string[] = [];
  const remote: string[] = [];
  for (const entry of graph.nodes) {
    const id = targetIdentity(entry.domain, entry.process, entry.version);
    if (nodes.has(id)) continue;
    const column = entry.origin === "Local" ? local : remote;
    nodes.set(id, { id, type: "process", position: { x: entry.origin === "Local" ? 0 : 390, y: column.length * 154 },
      sourcePosition: Position.Right, targetPosition: Position.Left,
      data: { domain: entry.domain, process: entry.process, version: entry.version,
        origin: entry.origin, status: "Resolved", steps: entry.origin === "Local"
          ? definitions.find(definition => definition.domain === entry.domain && definition.name === entry.process
            && definition.version === entry.version)?.steps.map(step => step.name) : undefined } });
    column.push(id);
  }

  const edges: ProcessEdge[] = graph.edges.map((entry, index) => {
    const source = identity(entry.source);
    const target = entry.target ? identity(entry.target)
      : `unresolved:${source}:${targetIdentity(entry.targetDomain, entry.targetProcess, entry.requestedVersion)}`;
    const candidates = !entry.target ? graph.failures.filter(item => identity(item.source) === source
      && item.targetDomain === entry.targetDomain && item.targetProcess === entry.targetProcess) : [];
    const versioned = candidates.filter(item => Object.hasOwn(item, "requestedVersion")
      && item.requestedVersion === entry.requestedVersion);
    const sameTargetEdges = graph.edges.filter(item => identity(item.source) === source
      && item.targetDomain === entry.targetDomain && item.targetProcess === entry.targetProcess);
    const legacy = candidates.filter(item => !Object.hasOwn(item, "requestedVersion"));
    const failure = versioned.length === 1 ? versioned[0]
      : versioned.length === 0 && sameTargetEdges.length === 1 && legacy.length === 1 ? legacy[0] : undefined;
    const reason = failure ? `${failure.kind}: ${failure.message}` : undefined;
    if (!entry.target && !nodes.has(target)) {
      nodes.set(target, { id: target, type: "process", position: { x: 390, y: remote.length * 154 },
        sourcePosition: Position.Right, targetPosition: Position.Left,
        data: { domain: entry.targetDomain, process: entry.targetProcess, version: entry.requestedVersion,
          origin: "Remote", status: "Unresolved", failure: reason } });
      remote.push(target);
    }
    return { id: `reference:${index}`, source, target, type: "smoothstep",
      label: entry.relationship ?? undefined, markerEnd: { type: MarkerType.ArrowClosed },
      data: { relationship: entry.relationship, source, target: entry.target ? identity(entry.target)
        : targetIdentity(entry.targetDomain, entry.targetProcess, entry.requestedVersion),
        status: entry.target ? "Resolved" : "Unresolved", failure: entry.target ? undefined : reason },
      className: entry.target ? "" : "bigpicture-edge-unresolved",
      style: { strokeWidth: 1.8, strokeDasharray: entry.target ? undefined : "6 4" },
      labelStyle: { fontSize: 12, fontWeight: 600 }, labelBgPadding: [8, 5], labelBgBorderRadius: 6 };
  });
  return { nodes: [...nodes.values()], edges };
}
