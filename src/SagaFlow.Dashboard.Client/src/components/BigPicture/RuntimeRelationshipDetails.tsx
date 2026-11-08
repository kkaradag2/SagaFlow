import type { InstanceRelationship } from "../../api/sagaflowApi";
import { CopyIdentity, OriginBadge } from "../InstanceTraceability";
import { processKey } from "./bigPictureMapping";

export function RuntimeRelationshipDetails({ row, onLocalInstanceOpen }: {
  row: InstanceRelationship; onLocalInstanceOpen?: (id: string) => void;
}) {
  const localId = row.perspective === "Source" ? row.sourceSagaInstanceId : row.targetSagaInstanceId;
  const fact = (label: string, value: string | null, copy = false) => <div>
    <dt className="text-xs text-zinc-500 dark:text-zinc-400">{label}</dt>
    <dd className="mt-1 break-all">{value ? copy ? <CopyIdentity label={label} value={value} /> : value : "Not known / not recorded"}</dd></div>;
  return <article aria-label={`Runtime invocation ${row.invocationId}`} className="space-y-4 border-t border-zinc-200 p-5 text-sm dark:border-zinc-800">
    <h3 className="font-semibold">{row.perspective} perspective</h3>
    {row.perspective === "Target" && <OriginBadge origin="External" />}
    {row.reconciliation && <div role="alert" className="rounded-lg border border-amber-400 bg-amber-50 p-3 text-amber-900 dark:bg-amber-950/40 dark:text-amber-200">
      <strong>Manual reconciliation required</strong><p className="mt-1 break-words">{row.reconciliation.reason}</p>
      <p className="mt-1">Required since {new Date(row.reconciliation.requiredAt).toLocaleString()}</p>
      <p className="mt-1 text-xs">A remote completion does not resolve this warning.</p>
    </div>}
    <dl className="space-y-4">
      {row.perspective === "Source" && row.rejectionReason && <div><dt className="text-xs text-zinc-500 dark:text-zinc-400">Rejection reason</dt><dd className="mt-1 whitespace-pre-wrap break-words">{row.rejectionReason}</dd></div>}
      {fact("Invocation ID", row.invocationId, true)}
      {fact("Source process", processKey(row.source))}
      {fact("Source Saga ID", row.sourceSagaInstanceId, true)}
      {fact("Source step ID", row.sourceStep, true)}
      {fact("Source correlation ID", row.sourceCorrelationId, true)}
      {fact("Target process", processKey(row.target))}
      {fact("Target Saga ID", row.targetSagaInstanceId, true)}
      {fact("Target correlation ID", row.targetCorrelationId, true)}
      {row.perspective === "Source" ? fact("Last Known Remote Status", row.lastKnownStatus) : fact("Target Local Status", row.targetLocalStatus)}
      {fact(row.perspective === "Target" ? "Source request created" : "Created", row.createdAt ? new Date(row.createdAt).toLocaleString() : null)}
      {fact(row.perspective === "Target" ? "Local Saga updated" : "Observation status updated", row.updatedAt ? new Date(row.updatedAt).toLocaleString() : null)}
      {row.perspective === "Source" && fact("Last observed locally", row.lastObservedAt ? new Date(row.lastObservedAt).toLocaleString() : null)}
    </dl>
    <p className="text-xs text-zinc-600 dark:text-zinc-400">{row.perspective === "Source"
      ? "Source domain’s last known observation, not live remote state. Accepted does not mean completed."
      : "Target status belongs to this local domain. Source and target correlations are independent."}</p>
    {localId && onLocalInstanceOpen && <button type="button" className="rounded-lg border border-zinc-300 px-3 py-2 dark:border-zinc-700"
      onClick={() => onLocalInstanceOpen(localId)}>Open local instance</button>}
    <p className="text-xs text-zinc-500">Remote Dashboard address is unavailable. Copy remote identity to locate its owning domain.</p>
  </article>;
}
