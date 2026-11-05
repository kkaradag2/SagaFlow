import { BaseEdge, EdgeLabelRenderer, getSmoothStepPath, type EdgeProps } from "@xyflow/react";
import type { ProcessEdge } from "./bigPictureMapping";

// Labels must sit above all SVG paths: sibling references can share the same initial segment.
export function ProcessRelationshipEdge({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition,
  markerEnd, markerStart, style, label, data }: EdgeProps<ProcessEdge>) {
  const [path, x, y] = getSmoothStepPath({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition });
  return <>
    <BaseEdge id={id} path={path} markerEnd={markerEnd} markerStart={markerStart} style={style} />
    {label != null && <EdgeLabelRenderer>
      <button type="button" onClick={data?.onInspect} data-edge-id={id}
        className="bigpicture-reference-label nodrag nopan absolute rounded-md bg-white px-2 py-1 text-xs font-semibold text-zinc-900 shadow-sm focus-visible:outline focus-visible:outline-2 focus-visible:outline-emerald-500 dark:bg-zinc-800 dark:text-zinc-100"
        style={{ transform: `translate(-50%, -50%) translate(${x}px, ${y}px)`, pointerEvents: "all", zIndex: 4 }}>
        {label}
      </button>
    </EdgeLabelRenderer>}
  </>;
}
