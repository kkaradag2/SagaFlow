import type { WorkflowNodeData } from "./workflowMapping";
import { comparisonValue, conditionLabel, operatorLabel } from "./workflowMapping";

export function WorkflowNodeDetails({ node }: { node: WorkflowNodeData }) {
  if (node.decision) {
    const { sourceStep, transition, index, nextRule, fallback, fallbackLabel } = node.decision;
    const condition = transition.condition;
    return <div className="space-y-5">
      <dl className="space-y-4 text-sm">
        <Field label="Source step" value={sourceStep.name} />
        <Field label="Outcome" value={transition.event} />
        <Field label="YAML transition" value={`#${index + 1}`} />
        {condition ? <><Field label="Path" value={condition.path} /><Field label="Operator" value={`${condition.operator} (${operatorLabel(condition.operator)})`} /><Field label="Value" value={comparisonValue(condition)} /></> :
          transition.when != null ? <Field label="Expression" value={transition.when} /> : null}
      </dl>
      <section><h4 className="mb-2 text-xs font-semibold uppercase tracking-wider text-zinc-500">Matching transition</h4>
        <div className="rounded-lg border border-emerald-500/30 p-3 text-sm text-emerald-700 [overflow-wrap:anywhere] dark:text-emerald-400">TRUE → {transition.nextStep}</div>
      </section>
      {nextRule ? <section><h4 className="mb-2 text-xs font-semibold uppercase tracking-wider text-zinc-500">Next rule when unmatched</h4>
        <p className="font-mono text-xs leading-5 [overflow-wrap:anywhere]">{conditionLabel(nextRule)}</p>
        <p className="mt-2 text-xs text-zinc-500">The remaining conditions are checked in YAML order before the fallback.</p>
      </section> : null}
      <section><h4 className="mb-2 text-xs font-semibold uppercase tracking-wider text-zinc-500">Fallback transition</h4>
        {fallback ? <div className="rounded-lg border border-zinc-200 p-3 text-sm [overflow-wrap:anywhere] dark:border-zinc-800">{fallbackLabel} → {fallback.nextStep}</div> : <p className="text-sm text-zinc-500">No fallback declared for this outcome.</p>}
        {fallback && fallbackLabel === "DEFAULT" ? <p className="mt-2 text-xs leading-5 text-zinc-500">Default applies after none of this outcome's conditions match.</p> : null}
      </section>
    </div>;
  }
  const step = node.step;
  return <div>
    {step ? <>
      <dl className="space-y-4 text-sm">
        <Field label="Step name" value={step.name} />
        <Field label="Command" value={step.command} />
        {step.timeoutLimit ? <Field label="Timeout" value={step.timeoutLimit} /> : null}
        <Field label="Max attempts (metadata)" value={String(step.maxAttempts)} />
        {step.compensation ? <Field label="Compensation command" value={step.compensation.command} /> : null}
      </dl>
      {step.maxAttempts === 1 ? <p className="mt-3 text-xs leading-5 text-zinc-500">The parsed model defaults maxAttempts to 1 when omitted.</p> : null}
      <h4 className="mb-3 mt-6 text-xs font-semibold uppercase tracking-wider text-zinc-500">Transitions · YAML order</h4>
      <ol className="space-y-3">{step.transitions.map((transition, index) => <li key={index} className="rounded-lg border border-zinc-200 p-3 text-xs dark:border-zinc-800">
        <div className="flex justify-between gap-2"><span className="font-semibold capitalize">{transition.event}</span><span className="text-zinc-400">#{index + 1}</span></div>
        <div className="mt-2 font-mono text-zinc-600 [overflow-wrap:anywhere] dark:text-zinc-300">{conditionLabel(transition) ?? "Default (unconditional fallback)"}</div>
        <div className={`mt-2 [overflow-wrap:anywhere] ${transition.nextStep === "compensation" ? "text-amber-600 dark:text-amber-400" : "text-emerald-600 dark:text-emerald-400"}`}>→ {transition.nextStep}</div>
        {transition.event === "timeout" ? <div className="mt-2 text-zinc-500">Declared timeout transition metadata.</div> : null}
      </li>)}</ol>
    </> : <><div className="font-semibold">{node.kind === "compensation" ? "Compensation" : "Completed"}</div>
      <p className="mt-3 text-sm leading-6 text-zinc-500">{node.kind === "compensation" ? "Reserved compensation target. Compensation commands remain attached to their steps; their execution order depends on the successful runtime path." : "Reserved successful completion target."}</p>
    </>}
  </div>;
}

function Field({ label, value }: { label: string; value: string }) {
  return <div><dt className="text-xs text-zinc-500">{label}</dt><dd className="mt-1 font-mono [overflow-wrap:anywhere]">{value}</dd></div>;
}
