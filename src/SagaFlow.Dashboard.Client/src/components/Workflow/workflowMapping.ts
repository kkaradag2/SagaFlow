import { MarkerType, Position, type Edge, type Node } from "@xyflow/react";
import type { SagaConditionDefinition, SagaDefinition, SagaStepDefinition, SagaTransitionDefinition } from "../../api/sagaflowApi";

export type WorkflowPort = { id: string; side: "NORTH" | "SOUTH" | "EAST" | "WEST"; x?: number; y?: number };
export type WorkflowNodeData = {
  kind: "step" | "condition" | "completed" | "compensation";
  name: string;
  step?: SagaStepDefinition;
  ordinal?: number;
  decision?: {
    sourceStep: SagaStepDefinition;
    transition: SagaTransitionDefinition;
    index: number;
    nextRule: SagaTransitionDefinition | null;
    fallback: SagaTransitionDefinition | null;
    fallbackLabel: "FALSE" | "DEFAULT";
  };
  incoming: WorkflowPort[];
  outgoing: WorkflowPort[];
};
export type WorkflowNode = Node<WorkflowNodeData, "workflow">;
export type WorkflowEdge = Edge<{
  kind: "transition" | "condition-entry" | "match" | "next-rule" | "fallback";
  event: string;
  transition: SagaTransitionDefinition | null;
}>;
export type WorkflowGraphData = { nodes: WorkflowNode[]; edges: WorkflowEdge[] };
export const CONDITION_NODE_SIZE = { width: 216, height: 128 };

const operators: Record<string, string> = { equals: "=", notEquals: "≠", greaterThan: ">", lessThan: "<" };

export const operatorLabel = (operator: string) => operators[operator] ?? operator;
export const comparisonValue = (condition: SagaConditionDefinition) => condition.valueJson ?? JSON.stringify(condition.value);

export function conditionLabel(transition: SagaTransitionDefinition): string | null {
  if (transition.condition) {
    const { path, operator } = transition.condition;
    return `${path} ${operatorLabel(operator)} ${comparisonValue(transition.condition)}`;
  }
  return transition.when;
}

export function shortConditionLabel(transition: SagaTransitionDefinition): string | null {
  const condition = transition.condition;
  if (!condition) return transition.when;
  const property = condition.path.split(".").at(-1) ?? condition.path;
  return `${property} ${operatorLabel(condition.operator)} ${comparisonValue(condition)}`;
}

export function transitionLabel(transition: SagaTransitionDefinition): string {
  const event = transition.event.charAt(0).toUpperCase() + transition.event.slice(1);
  return `${event} · ${conditionLabel(transition) ?? "Default"}`;
}

export function createWorkflowGraph(definition: SagaDefinition, { showFailurePaths = false }: { showFailurePaths?: boolean } = {}): WorkflowGraphData {
  const nodes: WorkflowNode[] = definition.steps.map((step, index) => ({
    id: `step:${step.name}`, type: "workflow", position: { x: 0, y: 0 },
    width: 184, height: 86, sourcePosition: Position.Right, targetPosition: Position.Left,
    data: { kind: "step", name: step.name, step, ordinal: index + 1, incoming: [], outgoing: [] },
  }));
  const nodesById = new Map(nodes.map(node => [node.id, node]));
  const edges: WorkflowEdge[] = [];

  function targetId(transition: SagaTransitionDefinition, step: SagaStepDefinition): string {
    const terminal = transition.nextStep === "completed" || transition.nextStep === "compensation";
    const target = `${terminal ? "terminal" : "step"}:${transition.nextStep}`;
    if (terminal && !nodesById.has(target)) {
      const node: WorkflowNode = {
        id: target, type: "workflow", position: { x: 0, y: 0 }, width: 168, height: 76,
        data: { kind: transition.nextStep as "completed" | "compensation", name: transition.nextStep, incoming: [], outgoing: [] },
      };
      nodes.push(node);
      nodesById.set(target, node);
    }
    if (!nodesById.has(target)) throw new Error(`Unknown transition target '${transition.nextStep}' in step '${step.name}'.`);
    return target;
  }

  function connect(id: string, source: string, target: string, label: string | undefined, event: string, kind: NonNullable<WorkflowEdge["data"]>["kind"], transition: SagaTransitionDefinition | null) {
    const sourceHandle = `out:${id}`;
    const targetHandle = `in:${id}`;
    const sourceNode = nodesById.get(source)!;
    // A decision matches to the right; a non-match leaves from the bottom tip.
    const nonMatch = sourceNode.data.kind === "condition" && (kind === "fallback" || kind === "next-rule");
    sourceNode.data.outgoing.push({ id: sourceHandle, side: nonMatch ? "SOUTH" : "EAST" });
    nodesById.get(target)!.data.incoming.push({ id: targetHandle, side: "WEST" });
    const color = event === "failure" ? "#f87171" : target === "terminal:compensation" ? "#f59e0b" : kind === "match" ? "#34d399" : kind === "next-rule" ? "#a78bfa" : "#94a3b8";
    edges.push({
      id, source, target, sourceHandle, targetHandle, type: "smoothstep", label,
      data: { event, kind, transition }, markerEnd: { type: MarkerType.ArrowClosed, color },
      style: { stroke: color, strokeWidth: 2, strokeDasharray: event === "failure" || kind === "next-rule" ? "5 4" : undefined },
      labelStyle: { fontSize: 11, fontWeight: 600 }, labelBgPadding: [7, 4], labelBgBorderRadius: 4,
      selectable: false, reconnectable: false,
    });
  }

  const conditional = (rule: SagaTransitionDefinition) => rule.condition != null || rule.when != null;
  for (const step of definition.steps) {
    // Timeout rules remain metadata: runtime outcomes route only success/failure.
    const events = [...new Set(step.transitions.filter(rule => rule.event === "success" || (showFailurePaths && rule.event === "failure")).map(rule => rule.event))];
    for (const event of events) {
      const rules = step.transitions.map((transition, index) => ({ transition, index })).filter(rule => rule.transition.event === event);
      const conditions = rules.filter(rule => conditional(rule.transition));
      const fallback = rules.find(rule => !conditional(rule.transition))?.transition ?? null;
      const source = `step:${step.name}`;
      if (conditions.length === 0) {
        if (fallback) connect(`transition:${step.name}:${rules[0].index}`, source, targetId(fallback, step), event === "failure" ? "FAILURE" : undefined, event, "transition", fallback);
        continue;
      }

      const decisions = conditions.map(({ transition, index }, position) => {
        const node: WorkflowNode = {
          id: `condition:${step.name}:${event}:${index}`, type: "workflow", position: { x: 0, y: 0 }, ...CONDITION_NODE_SIZE,
          data: { kind: "condition", name: conditionLabel(transition)!, incoming: [], outgoing: [],
            decision: { sourceStep: step, transition, index, nextRule: conditions[position + 1]?.transition ?? null, fallback,
              fallbackLabel: conditions.length === 1 ? "FALSE" : "DEFAULT" } },
        };
        nodes.push(node);
        nodesById.set(node.id, node);
        return node;
      });
      connect(`entry:${step.name}:${event}`, source, decisions[0].id, event === "failure" ? "FAILURE" : undefined, event, "condition-entry", null);
      decisions.forEach((node, position) => {
        const rule = conditions[position].transition;
        connect(`match:${node.id}`, node.id, targetId(rule, step), "TRUE", event, "match", rule);
        if (decisions[position + 1]) {
          // No match continues in YAML condition order. It is not a Boolean FALSE shortcut to the fallback.
          connect(`next:${node.id}`, node.id, decisions[position + 1].id, "NEXT RULE", event, "next-rule", null);
        } else if (fallback) {
          connect(`default:${node.id}`, node.id, targetId(fallback, step), node.data.decision!.fallbackLabel, event, "fallback", fallback);
        }
      });
    }
  }
  return { nodes, edges };
}
