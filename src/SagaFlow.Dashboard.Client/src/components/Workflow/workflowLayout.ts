import type { ELK, ElkNode } from "elkjs/lib/elk-api";
import type { WorkflowGraphData, WorkflowNode, WorkflowPort } from "./workflowMapping";

const portOrder = { NORTH: 0, EAST: 1, SOUTH: 2, WEST: 3 };

function diamondTip(node: WorkflowNode, port: WorkflowPort) {
  return {
    x: port.side === "WEST" ? 0 : port.side === "EAST" ? node.width! : node.width! / 2,
    y: port.side === "NORTH" ? 0 : port.side === "SOUTH" ? node.height! : node.height! / 2,
  };
}

export async function layoutWorkflow(graph: WorkflowGraphData, elk: Pick<ELK, "layout">): Promise<WorkflowGraphData> {
  const input: ElkNode = {
    id: "workflow",
    layoutOptions: {
      "elk.algorithm": "layered", "elk.direction": "RIGHT", "elk.edgeRouting": "ORTHOGONAL",
      "elk.spacing.nodeNode": "40", "elk.layered.spacing.nodeNodeBetweenLayers": "48",
      "elk.layered.considerModelOrder.strategy": "NODES_AND_EDGES",
      "elk.layered.crossingMinimization.forceNodeModelOrder": "true",
      "elk.layered.spacing.edgeNodeBetweenLayers": "16", "elk.spacing.edgeEdge": "16",
    },
    children: graph.nodes.map(node => ({
      id: node.id, width: node.width, height: node.height,
      layoutOptions: { "org.eclipse.elk.portConstraints": node.data.kind === "condition" ? "FIXED_POS" : "FIXED_ORDER" },
      ports: [
        ...[...node.data.incoming, ...node.data.outgoing].sort((a, b) => portOrder[a.side] - portOrder[b.side])
          .map((port, index) => ({
            id: `${node.id}/${port.id}`, width: node.data.kind === "condition" ? 0 : 2, height: node.data.kind === "condition" ? 0 : 2,
            ...(node.data.kind === "condition" ? diamondTip(node, port) : {}),
            layoutOptions: { "org.eclipse.elk.port.side": port.side, "org.eclipse.elk.port.index": String(index) },
          })),
      ],
    })),
    edges: graph.edges.map(edge => ({
      id: edge.id, sources: [`${edge.source}/${edge.sourceHandle}`], targets: [`${edge.target}/${edge.targetHandle}`],
      labels: edge.label ? [{ text: String(edge.label), width: String(edge.label).length * 7 + 14, height: 20 }] : [],
    })),
  };
  const layout = await elk.layout(input);
  const positions = new Map(layout.children?.map(node => [node.id, node]));
  return {
    nodes: graph.nodes.map(node => {
      const placed = positions.get(node.id);
      if (placed?.x == null || placed.y == null) throw new Error("Unable to lay out the workflow.");
      const ports = new Map(placed.ports?.map(port => [port.id, port]));
      const placePort = (port: WorkflowPort): WorkflowPort => {
        const position = ports.get(`${node.id}/${port.id}`);
        if (position?.x == null || position.y == null) throw new Error("Unable to place a workflow connection.");
        // Keep visual decision tips stable even if ELK mirrors a non-flow port while minimizing crossings.
        if (node.data.kind === "condition") return { ...port, ...diamondTip(node, port) };
        // React Flow's handle direction must follow the boundary ELK actually placed the port on.
        const side = position.x < 0 ? "WEST" : position.x >= node.width! ? "EAST" : position.y < 0 ? "NORTH" : "SOUTH";
        return { ...port, side, x: position.x, y: position.y };
      };
      return { ...node, position: { x: placed.x, y: placed.y }, data: { ...node.data,
        incoming: node.data.incoming.map(placePort),
        outgoing: node.data.outgoing.map(placePort),
      } };
    }),
    edges: graph.edges,
  };
}
