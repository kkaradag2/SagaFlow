import { Copy, Network } from "lucide-react";
import { useState, type ReactNode } from "react";
import type { InvocationView, ProcessIdentity, SagaInstanceDetail } from "../api/sagaflowApi";
function DateValue({ value }: { value: string | null }) { return <>{value ? new Date(value).toLocaleString() : "Not recorded"}</>; }
function Fact({ label, children }: { label: string; children: ReactNode }) {
  return <div className="min-w-0"><dt className="text-[11px] font-semibold uppercase tracking-wide text-zinc-500">{label}</dt><dd className="mt-1 break-all text-sm text-zinc-900 dark:text-zinc-100">{children}</dd></div>;
}
function Identity({ value }: { value: ProcessIdentity }) { return <code>{value.domain}/{value.process}@{value.version}</code>; }
export function CopyIdentity({ label, value }: { label: string; value: string }) {
  const [feedback, setFeedback] = useState<{ value: string; text: string } | null>(null);
  const copy = async () => {
    try { if (!navigator.clipboard) throw new Error("Clipboard unavailable"); await navigator.clipboard.writeText(value); setFeedback({ value, text: "Copied" }); }
    catch { setFeedback({ value, text: "Copy unavailable. Select and copy the identifier." }); }
  };
  return <span className="flex min-w-0 flex-wrap items-start gap-1.5"><code className="min-w-0 select-all break-all text-xs">{value}</code>
    <button type="button" aria-label={`Copy ${label}`} onClick={() => void copy()} className="shrink-0 rounded p-1 text-zinc-500 hover:bg-zinc-100 focus-visible:outline focus-visible:outline-2 focus-visible:outline-violet-500 dark:hover:bg-zinc-800"><Copy size={13} aria-hidden="true" /></button>
    {feedback?.value === value ? <span role="status" className="w-full text-xs text-zinc-500">{feedback.text}</span> : null}</span>;
}
export function OriginBadge({ origin = "Local" }: { origin?: "Local" | "External" }) {
  return <span className="inline-flex rounded border border-zinc-200 px-2 py-0.5 text-[10px] font-semibold text-zinc-600 dark:border-zinc-700 dark:text-zinc-300">Origin: {origin}</span>;
}
export function IncomingOrigin({ instance }: { instance: SagaInstanceDetail }) {
  const incoming = instance.incomingInvocation;
  if (!incoming) return null;
  return <section aria-label="External origin" className="rounded-2xl border border-violet-200 bg-violet-50/50 p-5 dark:border-violet-900 dark:bg-violet-950/20">
    <h3 className="flex items-center gap-2 text-sm font-semibold"><Network size={16} aria-hidden="true" />External origin</h3>
    <p className="mt-2 text-xs text-zinc-600 dark:text-zinc-400">This domain owns the local Saga. Source and local correlation IDs are independent.</p>
    <dl className="mt-4 grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
      <Fact label="Source process"><Identity value={incoming.source} /></Fact>
      <Fact label="Source Saga instance"><CopyIdentity label="source Saga instance ID" value={incoming.sourceSagaInstanceId} /></Fact>
      <Fact label="Source step">{incoming.sourceStep}</Fact>
      <Fact label="Invocation ID"><CopyIdentity label="incoming invocation ID" value={incoming.invocationId} /></Fact>
      <Fact label="Source correlation ID"><CopyIdentity label="source correlation ID" value={incoming.sourceCorrelationId} /></Fact>
      <Fact label="Target local Saga instance"><CopyIdentity label="target local Saga instance ID" value={instance.id} /></Fact>
      <Fact label="Target local correlation ID"><CopyIdentity label="target local correlation ID" value={instance.correlationId} /></Fact>
      <Fact label="Source request created"><DateValue value={incoming.sourceRequestCreatedAt} /></Fact>
      <Fact label="Accepted locally"><DateValue value={incoming.acceptedAt} /></Fact>
    </dl>
    <p className="mt-4 text-xs text-zinc-500">Remote Dashboard address is unavailable. Copy the source identity to locate its owning domain.</p>
  </section>;
}
export function ReconciliationAlert({ invocation, stepName, localStatus }: { invocation: InvocationView; stepName: string; localStatus: string }) {
  if (!invocation.reconciliation) return null;
  return <div role="alert" className="border-t border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-900 dark:bg-amber-950/30 dark:text-amber-200">
    <strong>Manual reconciliation required</strong><p className="mt-1 break-words">{invocation.reconciliation.reason}</p>
    <p className="mt-1 break-all text-xs">Local step {stepName}: {localStatus} · Last known external status: {invocation.lastKnownStatus ?? "Not recorded"}</p>
    <p className="mt-1 break-all font-mono text-xs">Invocation: {invocation.invocationId}</p>
    <p className="mt-1 text-xs">Required since <DateValue value={invocation.reconciliation.requiredAt} />. A remote Completed observation does not resolve this warning.</p>
  </div>;
}
export function InvocationDetails({ invocation, localStatus }: { invocation: InvocationView; localStatus: string }) {
  return <section aria-label="External invocation relationship" className="mb-4 rounded-xl border border-violet-200 bg-violet-50/50 p-4 dark:border-violet-900 dark:bg-violet-950/20">
    <h5 className="text-sm font-semibold">External invocation relationship</h5>
    <dl className="mt-3 grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
      <Fact label="Local step status">{localStatus}</Fact>
      <Fact label="Target process"><Identity value={invocation.target} /></Fact>
      <Fact label="Last known external status"><strong>{invocation.lastKnownStatus ?? "Not recorded"}</strong></Fact>
      <Fact label="Invocation ID"><CopyIdentity label="invocation ID" value={invocation.invocationId} /></Fact>
      <Fact label="Target Saga instance">{invocation.targetSagaInstanceId ? <CopyIdentity label="target Saga instance ID" value={invocation.targetSagaInstanceId} /> : "Not known yet"}</Fact>
      <Fact label="Created"><DateValue value={invocation.createdAt} /></Fact>
      <Fact label="Status updated"><DateValue value={invocation.updatedAt} /></Fact>
      <Fact label="Last observed locally"><DateValue value={invocation.lastObservedAt} /></Fact>
    </dl>
    <p className="mt-3 text-xs text-zinc-600 dark:text-zinc-400">This is the source domain’s last known observation, not live remote state. Accepted does not mean completed.</p>
    <p className="mt-2 text-xs text-zinc-500">Remote Dashboard address is unavailable. Copy the target identity to locate its owning domain.</p>
  </section>;
}
