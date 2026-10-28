import {
  ArrowLeft,
  Boxes,
  CheckCircle2,
  ChevronRight,
  Circle,
  Clock3,
  RefreshCw,
  TimerOff,
  XCircle,
} from "lucide-react";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";

import { EmptyPanel } from "../layout/DashboardLayout";
import {
  getInstance,
  getInstances,
  type SagaInstanceDetail,
  type SagaInstanceSummary,
  type SagaStep,
} from "../api/sagaflowApi";

export function InstancesPage() {
  const [instances, setInstances] = useState<SagaInstanceSummary[]>([]);
  const [selected, setSelected] = useState<SagaInstanceDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const selectedId = selected?.id;

  const loadInstances = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setInstances(await getInstances());
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : "Unable to load Saga instances.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void loadInstances(); }, [loadInstances]);

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [nextInstances, nextDetail] = await Promise.all([
        getInstances(),
        selectedId ? getInstance(selectedId) : Promise.resolve(null),
      ]);
      setInstances(nextInstances);
      if (nextDetail) setSelected(nextDetail);
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : "Unable to refresh Saga state.");
    } finally {
      setLoading(false);
    }
  }, [selectedId]);

  const scrollToTop = () => {
    requestAnimationFrame(() => document.getElementById("dashboard-content")?.scrollTo({ top: 0, behavior: "smooth" }));
  };

  const openInstance = async (instanceId: string) => {
    setLoading(true);
    setError(null);
    try {
      setSelected(await getInstance(instanceId));
      scrollToTop();
    } catch (requestError) {
      setError(requestError instanceof Error ? requestError.message : "Unable to load Saga instance.");
    } finally {
      setLoading(false);
    }
  };

  const closeInstance = () => {
    setSelected(null);
    scrollToTop();
  };

  return <div className="space-y-5">
    {error ? <div role="alert" className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-700 dark:border-red-900 dark:bg-red-950/30 dark:text-red-300">{error}</div> : null}
    {selected ? (
      <InstanceDetail instance={selected} loading={loading} onBack={closeInstance} onRefresh={refresh} />
    ) : (
      <InstanceList instances={instances} loading={loading} onOpen={openInstance} onRefresh={refresh} />
    )}
  </div>;
}

function InstanceList({ instances, loading, onOpen, onRefresh }: {
  instances: SagaInstanceSummary[];
  loading: boolean;
  onOpen: (id: string) => void;
  onRefresh: () => Promise<void>;
}) {
  const running = instances.filter(instance => instance.status === "Running").length;

  return <>
    <div className="flex flex-wrap items-center justify-between gap-4">
      <div>
        <div className="flex items-center gap-2"><Boxes size={18} className="text-emerald-500" /><h2 className="text-lg font-semibold">Saga instances</h2></div>
        <p className="mt-1 text-sm text-zinc-500">{instances.length} total · {running} running</p>
      </div>
      <RefreshButton loading={loading} onClick={onRefresh} />
    </div>
    {instances.length === 0 && !loading ? (
      <EmptyPanel icon={<Boxes size={20} strokeWidth={2} aria-hidden="true" />} title="No saga instances."
        description="Running and completed local orchestration instances will appear here." />
    ) : (
      <div className="overflow-hidden rounded-2xl border border-zinc-200 bg-white shadow-sm dark:border-zinc-800 dark:bg-zinc-950/40">
        <div className="hidden grid-cols-[minmax(230px,1.4fr)_minmax(150px,0.8fr)_minmax(140px,0.7fr)_minmax(190px,0.9fr)_32px] gap-4 border-b border-zinc-200 bg-zinc-50 px-5 py-3 text-[11px] font-semibold uppercase tracking-[0.12em] text-zinc-500 dark:border-zinc-800 dark:bg-zinc-900/60 lg:grid">
          <span>Process / Instance</span><span>State</span><span>Current step</span><span>Updated</span><span />
        </div>
        <div className="divide-y divide-zinc-100 dark:divide-zinc-900">
          {instances.map(instance => <button key={instance.id} type="button" onClick={() => onOpen(instance.id)}
            className="grid w-full gap-3 px-5 py-4 text-left transition hover:bg-zinc-50 focus-visible:outline focus-visible:outline-2 focus-visible:outline-inset focus-visible:outline-emerald-500 dark:hover:bg-zinc-900/50 lg:grid-cols-[minmax(230px,1.4fr)_minmax(150px,0.8fr)_minmax(140px,0.7fr)_minmax(190px,0.9fr)_32px] lg:items-center lg:gap-4">
            <div className="min-w-0"><div className="font-semibold text-zinc-950 dark:text-zinc-100">{instance.processName}</div><div className="mt-1 truncate font-mono text-xs text-zinc-500" title={instance.id}>{instance.id}</div></div>
            <div><Status value={instance.status} /></div>
            <div className="text-sm"><span className="mr-2 text-xs text-zinc-500 lg:hidden">Current:</span>{instance.currentStep ?? "—"}</div>
            <div className="text-sm text-zinc-600 dark:text-zinc-400"><div>{formatDate(instance.updatedAt)}</div><div className="mt-0.5 text-xs text-zinc-400">Created {formatDate(instance.createdAt)}</div></div>
            <ChevronRight size={18} className="hidden text-zinc-400 lg:block" aria-hidden="true" />
          </button>)}
        </div>
      </div>
    )}
  </>;
}

function InstanceDetail({ instance, loading, onBack, onRefresh }: {
  instance: SagaInstanceDetail;
  loading: boolean;
  onBack: () => void;
  onRefresh: () => Promise<void>;
}) {
  const completedSteps = instance.steps.filter(step => step.status === "Completed").length;
  const [now, setNow] = useState(() => Date.now());
  const refreshedDeadlines = useRef(new Set<string>());
  const countdownKey = instance.steps
    .filter(canShowCountdown)
    .map(step => `${step.stepName}:${step.startedAt}:${step.timeoutLimit}`)
    .join("|");

  useEffect(() => {
    refreshedDeadlines.current.clear();
  }, [instance.id]);

  useEffect(() => {
    setNow(Date.now());
    if (!countdownKey) return;

    const interval = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(interval);
  }, [countdownKey]);

  useEffect(() => {
    let refreshRequired = false;

    for (const step of instance.steps.filter(canShowCountdown)) {
      const deadline = getDeadline(step);
      const deadlineKey = `${instance.id}:${step.stepName}:${step.startedAt}:${step.timeoutLimit}`;
      if (deadline !== null && deadline <= now && !refreshedDeadlines.current.has(deadlineKey)) {
        refreshedDeadlines.current.add(deadlineKey);
        refreshRequired = true;
      }
    }

    if (refreshRequired) void onRefresh();
  }, [instance.id, instance.steps, now, onRefresh]);

  return <>
    <div className="flex flex-wrap items-center justify-between gap-3">
      <button type="button" onClick={onBack} className="inline-flex items-center gap-2 rounded-lg px-1 py-2 text-sm font-medium text-zinc-600 hover:text-zinc-950 focus-visible:outline focus-visible:outline-2 focus-visible:outline-emerald-500 dark:text-zinc-400 dark:hover:text-white"><ArrowLeft size={16} /> All instances</button>
      <RefreshButton loading={loading} onClick={onRefresh} />
    </div>

    <section className="overflow-hidden rounded-2xl border border-zinc-200 bg-white shadow-sm dark:border-zinc-800 dark:bg-zinc-950/40">
      <div className="flex flex-wrap items-start justify-between gap-5 border-b border-zinc-100 px-5 py-5 dark:border-zinc-900 sm:px-6">
        <div className="min-w-0"><div className="flex flex-wrap items-center gap-3"><h2 className="text-xl font-bold tracking-tight text-zinc-950 dark:text-white">{instance.processName}</h2><Status value={instance.status} /></div><div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-zinc-500"><span>Instance</span><code className="break-all rounded-md bg-zinc-100 px-2 py-1 font-mono dark:bg-zinc-900">{instance.id}</code></div></div>
        <div className="min-w-24 rounded-xl bg-zinc-50 px-4 py-3 text-right dark:bg-zinc-900/70"><div className="text-2xl font-bold tabular-nums">{completedSteps}<span className="text-base font-medium text-zinc-500">/{instance.steps.length}</span></div><div className="text-[11px] font-medium uppercase tracking-wide text-zinc-500">steps completed</div></div>
      </div>
      <dl className="grid gap-x-8 gap-y-4 px-5 py-5 sm:grid-cols-2 sm:px-6 lg:grid-cols-4">
        <Fact label="Current step" value={instance.currentStep ?? "—"} accent />
        <Fact label="Correlation ID" value={instance.correlationId} mono />
        <Fact label="Created" value={formatDate(instance.createdAt)} />
        <Fact label="Last updated" value={formatDate(instance.updatedAt)} />
      </dl>
      {instance.failureReason ? <div className="mx-5 mb-5 rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-700 dark:border-red-900 dark:bg-red-950/30 dark:text-red-300 sm:mx-6"><strong>Failure:</strong> {instance.failureReason}</div> : null}
    </section>

    <section aria-labelledby="steps-heading">
      <div className="mb-4 flex items-end justify-between gap-4"><div><h3 id="steps-heading" className="text-base font-bold">Execution flow</h3><p className="mt-1 text-sm text-zinc-500">Steps are shown in YAML definition order.</p></div><span className="text-xs text-zinc-500">{instance.steps.length} steps</span></div>
      <ol className="space-y-0">
        {instance.steps.map((step, index) => <StepItem key={step.stepName} step={step} index={index} isLast={index === instance.steps.length - 1} isCurrent={instance.currentStep === step.stepName} now={now} />)}
      </ol>
    </section>

    {instance.compensations.length > 0 ? <section aria-labelledby="compensation-heading"><h3 id="compensation-heading" className="mb-3 text-base font-bold">Compensation plan</h3><div className="overflow-hidden rounded-2xl border border-zinc-200 bg-white dark:border-zinc-800 dark:bg-zinc-950/40"><div className="divide-y divide-zinc-100 dark:divide-zinc-900">{instance.compensations.map(item => <div key={item.stepId} className="grid gap-2 px-5 py-4 sm:grid-cols-[48px_1fr_auto] sm:items-center"><span className="text-sm font-semibold text-zinc-500">#{item.executionOrder ?? "—"}</span><div><div className="font-medium">{item.stepId}</div><div className="mt-0.5 font-mono text-xs text-zinc-500">{item.command}</div></div><Status value={item.status} /></div>)}</div></div></section> : null}
  </>;
}

function StepItem({ step, index, isLast, isCurrent, now }: { step: SagaStep; index: number; isLast: boolean; isCurrent: boolean; now: number }) {
  const hasInput = step.input != null;
  const hasResult = step.result != null;
  const isPending = step.status === "Pending";
  const isTimedOut = step.status === "TimedOut";
  const remaining = canShowCountdown(step) ? getRemaining(step, now) : null;
  const cardClass = isTimedOut
    ? "border-orange-400/70 shadow-sm shadow-orange-500/10 dark:border-orange-800"
    : isCurrent
      ? "border-emerald-400/70 shadow-sm shadow-emerald-500/10 dark:border-emerald-700"
      : "border-zinc-200 dark:border-zinc-800";

  return <li className="relative grid grid-cols-[36px_minmax(0,1fr)] gap-3 sm:grid-cols-[44px_minmax(0,1fr)] sm:gap-4">
    {!isLast ? <span className="absolute left-[17px] top-9 h-[calc(100%-20px)] w-px bg-zinc-200 dark:bg-zinc-800 sm:left-[21px]" aria-hidden="true" /> : null}
    <span className={`relative z-10 mt-4 inline-flex size-9 items-center justify-center rounded-full border sm:size-11 ${stepMarkerClass(step.status)}`}>{stepIcon(step.status, index)}</span>
    <article className={`mb-3 rounded-2xl border bg-white transition dark:bg-zinc-950/40 ${cardClass}`}>
      <div className={`flex flex-wrap items-center justify-between gap-3 ${isPending ? "px-4 py-4" : "border-b border-zinc-100 px-4 py-4 dark:border-zinc-900 sm:px-5"}`}>
        <div className="min-w-0"><div className="flex flex-wrap items-center gap-2"><h4 className="font-semibold">{step.stepName}</h4>{isCurrent ? <span className="rounded-full bg-emerald-500/10 px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide text-emerald-700 dark:text-emerald-300">Current</span> : null}</div><div className="mt-1 truncate font-mono text-xs text-zinc-500">{step.command}</div></div>
        <Status value={step.status} />
      </div>
      {!isPending ? <div className="px-4 py-4 sm:px-5">
        <dl className={`grid gap-x-5 gap-y-4 sm:grid-cols-2 ${remaining !== null ? "xl:grid-cols-6" : "xl:grid-cols-5"}`}><Fact label="Execution order" value={step.executionOrder?.toString() ?? "—"} /><Fact label="Timeout limit" value={step.timeoutLimit ?? "—"} /><Fact label="Dispatched" value={formatDate(step.startedAt)} />{remaining !== null ? <CountdownFact milliseconds={remaining} /> : null}<Fact label="Completed" value={formatDate(step.completedAt)} /><Fact label="Timed out at" value={formatDate(step.timedOutAt)} /></dl>
        {isTimedOut ? <div className="mt-4 flex items-start gap-3 rounded-xl border border-orange-200 bg-orange-50 px-3.5 py-3 text-sm text-orange-800 dark:border-orange-900 dark:bg-orange-950/30 dark:text-orange-200"><TimerOff size={17} className="mt-0.5 shrink-0" aria-hidden="true" /><div><div className="font-semibold">Step timed out</div><div className="mt-0.5 text-xs text-orange-700/80 dark:text-orange-300/80">No valid outcome was received before {formatDate(step.timedOutAt)}.</div></div></div> : null}
        {hasInput || hasResult ? <div className="mt-4 grid gap-3 xl:grid-cols-2">{hasInput ? <PayloadPanel label="Input payload" value={step.input} /> : null}{hasResult ? <PayloadPanel label="Result" value={step.result} /> : null}</div> : null}
      </div> : null}
    </article>
  </li>;
}

function PayloadPanel({ label, value }: { label: string; value: unknown }) {
  return <details className="group overflow-hidden rounded-xl border border-zinc-200 bg-zinc-50 dark:border-zinc-800 dark:bg-zinc-950">
    <summary className="flex cursor-pointer list-none items-center justify-between gap-3 px-3.5 py-2.5 text-xs font-semibold text-zinc-700 marker:hidden transition hover:bg-zinc-100 dark:text-zinc-200 dark:hover:bg-zinc-900"><span className="flex items-center gap-2"><span>{label}</span><span className="rounded bg-zinc-200 px-1.5 py-0.5 font-mono text-[9px] tracking-wide text-zinc-500 dark:bg-zinc-800">JSON</span></span><ChevronRight size={14} className="shrink-0 transition group-open:rotate-90" aria-hidden="true" /></summary>
    <pre className="max-h-72 overflow-auto border-t border-zinc-200 bg-zinc-950 p-4 font-mono text-xs leading-5 text-zinc-100 dark:border-zinc-800">{JSON.stringify(value, null, 2)}</pre>
  </details>;
}

function CountdownFact({ milliseconds }: { milliseconds: number }) {
  return <div className="rounded-lg bg-blue-50 px-3 py-2 dark:bg-blue-950/30"><dt className="text-[11px] font-semibold uppercase tracking-[0.1em] text-blue-600 dark:text-blue-300">Remaining</dt><dd className="mt-1 font-mono text-base font-bold tabular-nums text-blue-700 dark:text-blue-200" aria-live="off">{formatCountdown(milliseconds)}</dd></div>;
}

function RefreshButton({ loading, onClick }: { loading: boolean; onClick: () => Promise<void> }) {
  return <button type="button" onClick={() => void onClick()} disabled={loading} className="inline-flex items-center gap-2 rounded-xl border border-zinc-200 bg-white px-3.5 py-2 text-sm font-medium shadow-sm transition hover:bg-zinc-50 disabled:opacity-50 dark:border-zinc-800 dark:bg-zinc-950 dark:hover:bg-zinc-900"><RefreshCw size={15} className={loading ? "animate-spin" : ""} aria-hidden="true" /> Refresh</button>;
}

function Fact({ label, value, mono = false, accent = false }: { label: string; value: string; mono?: boolean; accent?: boolean }) {
  return <div><dt className="text-[11px] font-semibold uppercase tracking-[0.1em] text-zinc-500">{label}</dt><dd className={`mt-1 break-all text-sm ${mono ? "font-mono text-xs" : ""} ${accent ? "font-semibold text-emerald-700 dark:text-emerald-300" : "text-zinc-900 dark:text-zinc-100"}`}>{value}</dd></div>;
}

function Status({ value }: { value: string }) {
  return <span className={`inline-flex items-center rounded-full border px-2.5 py-1 text-xs font-semibold ${statusClass(value)}`}>{value}</span>;
}

function statusClass(status: string) {
  switch (status) {
    case "Running": case "Dispatched": return "border-blue-200 bg-blue-50 text-blue-700 dark:border-blue-900 dark:bg-blue-950/50 dark:text-blue-300";
    case "Completed": case "Compensated": return "border-emerald-200 bg-emerald-50 text-emerald-700 dark:border-emerald-900 dark:bg-emerald-950/50 dark:text-emerald-300";
    case "Failed": return "border-red-200 bg-red-50 text-red-700 dark:border-red-900 dark:bg-red-950/50 dark:text-red-300";
    case "CompensationRequested": case "Compensating": case "Planned": return "border-amber-200 bg-amber-50 text-amber-700 dark:border-amber-900 dark:bg-amber-950/50 dark:text-amber-300";
    case "TimedOut": return "border-orange-200 bg-orange-50 text-orange-700 dark:border-orange-900 dark:bg-orange-950/50 dark:text-orange-300";
    default: return "border-zinc-200 bg-zinc-50 text-zinc-600 dark:border-zinc-700 dark:bg-zinc-900 dark:text-zinc-300";
  }
}

function stepMarkerClass(status: string) {
  switch (status) {
    case "Completed": return "border-emerald-500 bg-emerald-500 text-white";
    case "Dispatched": return "border-blue-400 bg-blue-50 text-blue-600 dark:bg-blue-950 dark:text-blue-300";
    case "Failed": return "border-red-500 bg-red-500 text-white";
    case "TimedOut": return "border-orange-500 bg-orange-50 text-orange-600 dark:bg-orange-950 dark:text-orange-300";
    default: return "border-zinc-300 bg-white text-zinc-400 dark:border-zinc-700 dark:bg-zinc-950 dark:text-zinc-600";
  }
}

function stepIcon(status: string, index: number): ReactNode {
  if (status === "Completed") return <CheckCircle2 size={19} aria-label="Completed" />;
  if (status === "Dispatched") return <Clock3 size={18} aria-label="Dispatched" />;
  if (status === "Failed") return <XCircle size={19} aria-label="Failed" />;
  if (status === "TimedOut") return <TimerOff size={19} aria-label="Timed out" />;
  return <span className="relative"><Circle size={18} aria-hidden="true" /><span className="sr-only">Pending step {index + 1}</span></span>;
}

function formatDate(value: string | null) { return value ? new Date(value).toLocaleString() : "—"; }

function canShowCountdown(step: SagaStep) {
  return step.status === "Dispatched" && step.startedAt !== null && step.timeoutLimit !== null;
}

function getDeadline(step: SagaStep) {
  if (!step.startedAt || !step.timeoutLimit) return null;
  const startedAt = Date.parse(step.startedAt);
  const timeout = parseDuration(step.timeoutLimit);
  return Number.isFinite(startedAt) && timeout !== null ? startedAt + timeout : null;
}

function getRemaining(step: SagaStep, now: number) {
  const deadline = getDeadline(step);
  return deadline === null ? 0 : Math.max(0, deadline - now);
}

function parseDuration(value: string) {
  const timeSpan = value.trim().match(/^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$/);
  if (timeSpan) {
    const [, days = "0", hours, minutes, seconds, fraction = ""] = timeSpan;
    const fractionMilliseconds = Number(`0.${fraction || "0"}`) * 1000;
    return (((Number(days) * 24 + Number(hours)) * 60 + Number(minutes)) * 60 + Number(seconds)) * 1000 + fractionMilliseconds;
  }

  const simple = value.trim().match(/^(\d+(?:\.\d+)?)\s*(ms|s|m|h)$/i);
  if (!simple) return null;
  const factors: Record<string, number> = { ms: 1, s: 1000, m: 60_000, h: 3_600_000 };
  return Number(simple[1]) * factors[simple[2].toLowerCase()];
}

function formatCountdown(milliseconds: number) {
  const totalSeconds = Math.max(0, Math.ceil(milliseconds / 1000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${minutes.toString().padStart(2, "0")}:${seconds.toString().padStart(2, "0")}`;
}
