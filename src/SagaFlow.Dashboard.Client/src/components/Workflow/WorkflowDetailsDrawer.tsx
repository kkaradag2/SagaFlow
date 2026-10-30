import { X } from "lucide-react";
import { useEffect, useId, useRef } from "react";
import { createPortal } from "react-dom";
import type { WorkflowNodeData } from "./workflowMapping";
import { WorkflowNodeDetails } from "./WorkflowNodeDetails";

export function WorkflowDetailsDrawer({ node, onClose }: { node: WorkflowNodeData | null; onClose: () => void }) {
  const panel = useRef<HTMLElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const titleId = useId();
  const open = node !== null;

  useEffect(() => {
    if (!open) return;
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const drawer = panel.current;
    closeButton.current?.focus({ preventScroll: true });
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); onClose(); }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => {
      window.removeEventListener("keydown", onKeyDown);
      if (previousFocus?.isConnected && (document.activeElement === document.body || drawer?.contains(document.activeElement)))
        previousFocus.focus({ preventScroll: true });
    };
  }, [open, onClose]);

  if (!node) return null;
  const title = node.kind === "condition" ? "Condition details" : node.kind === "step" ? "Step details" : "Terminal details";
  return createPortal(<aside ref={panel} role="dialog" aria-modal={false} aria-labelledby={titleId}
    className="workflow-details-drawer fixed inset-y-0 right-0 z-[70] flex w-[min(420px,100vw)] min-w-0 flex-col border-l border-zinc-200 bg-white shadow-2xl dark:border-zinc-800 dark:bg-zinc-950">
    <header className="flex shrink-0 items-start justify-between gap-3 border-b border-zinc-200 px-5 py-4 dark:border-zinc-800">
      <div className="min-w-0"><h3 id={titleId} className="text-sm font-semibold">{title}</h3>
        <p className="mt-1 truncate font-mono text-xs text-zinc-500">{node.decision?.sourceStep.name ?? node.name}</p>
      </div>
      <button ref={closeButton} type="button" onClick={onClose} aria-label="Close node details" title="Close (Escape)" className="shrink-0 rounded-lg p-1.5 text-zinc-500 hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-emerald-500 dark:hover:bg-zinc-800"><X size={16} /></button>
    </header>
    <div key={node.decision ? `condition:${node.decision.sourceStep.name}:${node.decision.index}` : `${node.kind}:${node.name}`}
      className="min-h-0 flex-1 overflow-y-auto overscroll-contain p-5"><WorkflowNodeDetails node={node} /></div>
  </aside>, document.body);
}
