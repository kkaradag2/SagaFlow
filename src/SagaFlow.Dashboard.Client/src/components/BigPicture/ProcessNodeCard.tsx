import { Handle, Position, type NodeProps } from "@xyflow/react";
import type { ProcessNode } from "./bigPictureMapping";

export function ProcessNodeCard({ data, selected }: NodeProps<ProcessNode>) {
  const unresolved = data.status === "Unresolved";
  return <div className={`w-[244px] rounded-xl border bg-white px-4 py-3 text-left shadow-sm transition dark:bg-zinc-900
    ${selected ? "border-emerald-500 ring-2 ring-emerald-500/25" : unresolved
      ? "border-amber-400 dark:border-amber-600" : "border-zinc-300 dark:border-zinc-700"}`}
    aria-label={`${data.domain} ${data.process} version ${data.version ?? "unspecified"}, ${data.origin}, ${data.status}`}>
    <Handle type="target" position={Position.Left} className="!size-2 !border-0 !bg-zinc-400 dark:!bg-zinc-500" />
    <div className="truncate text-[11px] font-semibold uppercase tracking-wide text-zinc-600 dark:text-zinc-300">{data.domain}</div>
    <div className="mt-1 truncate text-sm font-semibold text-zinc-950 dark:text-zinc-50" title={data.process}>{data.process}</div>
    <div className="mt-2 text-xs text-zinc-600 dark:text-zinc-300">Version {data.version ?? "unspecified"}</div>
    {Boolean(data.runtimeCount) && <div className="mt-1 text-[11px] font-semibold text-violet-700 dark:text-violet-300">Instance context · {data.runtimeCount} loaded</div>}
    <div className="mt-2 flex items-center gap-1.5 border-t border-zinc-100 pt-2 text-[11px] dark:border-zinc-800">
      <span className="font-medium text-zinc-700 dark:text-zinc-200">{data.origin}</span>
      <span className="text-zinc-400">·</span>
      <span className={unresolved ? "font-medium text-amber-700 dark:text-amber-300" : "text-emerald-700 dark:text-emerald-300"}>{data.status}</span>
    </div>
    <Handle type="source" position={Position.Right} className="!size-2 !border-0 !bg-zinc-400 dark:!bg-zinc-500" />
  </div>;
}
