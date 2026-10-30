import { Handle, Position, type NodeProps } from "@xyflow/react";
import { CheckCircle2, GitBranch, RotateCcw } from "lucide-react";
import { memo } from "react";
import type { WorkflowNode, WorkflowPort } from "./workflowMapping";
import { CONDITION_NODE_SIZE, shortConditionLabel } from "./workflowMapping";

const positions = { NORTH: Position.Top, SOUTH: Position.Bottom, EAST: Position.Right, WEST: Position.Left };

export const WorkflowNodeCard = memo(function WorkflowNodeCard({ data, selected, width, height }: NodeProps<WorkflowNode>) {
  const terminal = data.kind === "completed" || data.kind === "compensation";
  const compensation = data.kind === "compensation";
  if (data.kind === "condition") {
    const shape = { width: width ?? CONDITION_NODE_SIZE.width, height: height ?? CONDITION_NODE_SIZE.height };
    return <div data-workflow-kind="condition" data-selected={selected} title={data.name} className="workflow-condition relative h-full w-full">
      <div className="workflow-condition-outline" /><div className="workflow-condition-surface" />
      <div className="relative flex h-full items-center justify-center">
        <div className="line-clamp-3 w-[70%] text-center font-mono text-xs font-semibold leading-5 text-violet-900 [overflow-wrap:anywhere] dark:text-violet-100">
          {data.decision ? shortConditionLabel(data.decision.transition) : data.name}
        </div>
      </div>
      {data.incoming.map(port => <Port key={port.id} port={port} type="target" diamond={shape} />)}
      {data.outgoing.map(port => <Port key={port.id} port={port} type="source" diamond={shape} />)}
    </div>;
  }
  const Icon = compensation ? RotateCcw : terminal ? CheckCircle2 : GitBranch;
  return <div data-workflow-kind={data.kind} title={data.step?.command}
    className={`h-full w-full rounded-xl border bg-white p-3 shadow-sm dark:bg-zinc-950 ${selected ? "border-emerald-500 ring-2 ring-emerald-500/25" : compensation ? "border-amber-500/60" : "border-zinc-300 dark:border-zinc-700"}`}>
    {data.incoming.map(port => <Port key={port.id} port={port} type="target" />)}
    <div className="mb-1.5 flex items-center justify-between gap-2">
      <span className={`inline-flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-wider ${compensation ? "text-amber-600 dark:text-amber-400" : "text-emerald-600 dark:text-emerald-400"}`}>
        <Icon size={12} />{terminal ? compensation ? "Compensation" : "End" : null}
      </span>
      {data.step?.compensation ? <span title={`Compensation: ${data.step.compensation.command}`}><RotateCcw size={11} className="text-amber-600 dark:text-amber-400" /></span> : null}
    </div>
    <div className="truncate text-[13px] font-semibold leading-5 text-zinc-950 dark:text-zinc-100">{terminal ? compensation ? "Compensation" : "Completed" : data.step?.command}</div>
    <div className="mt-1 truncate font-mono text-[11px] text-zinc-500 dark:text-zinc-400">{terminal ? `nextStep: ${data.name}` : data.name}</div>
    {data.outgoing.map(port => <Port key={port.id} port={port} type="source" />)}
  </div>;
});

function Port({ port, type, diamond }: { port: WorkflowPort; type: "source" | "target"; diamond?: { width: number; height: number } }) {
  let left = port.x;
  let top = port.y;
  // Attach library-rendered edges to the diamond outline rather than its rectangular bounding box.
  if (diamond && left != null && top != null) {
    if (port.side === "EAST" || port.side === "WEST") {
      const inset = diamond.width / 2 * Math.abs(1 - 2 * Math.max(0, Math.min(top, diamond.height)) / diamond.height);
      left = port.side === "WEST" ? inset : diamond.width - inset;
    } else {
      const inset = diamond.height / 2 * Math.abs(1 - 2 * Math.max(0, Math.min(left, diamond.width)) / diamond.width);
      top = port.side === "NORTH" ? inset : diamond.height - inset;
    }
  }
  return <Handle id={port.id} type={type} position={positions[port.side]} isConnectable={false}
    style={{ left, top, right: "auto", bottom: "auto", transform: "translate(-50%, -50%)" }} />;
}
