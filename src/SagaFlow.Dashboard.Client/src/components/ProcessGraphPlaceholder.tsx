export function ProcessGraphPlaceholder() {
  return (
    <article className="rounded-2xl border border-zinc-200 bg-white p-6 shadow-sm dark:border-zinc-800 dark:bg-zinc-950/40 dark:shadow-none">
      <div className="mb-5 flex items-center justify-between gap-4">
        <div>
          <h2 className="m-0 text-sm font-semibold text-zinc-950 dark:text-zinc-100">
            Purchase Process
          </h2>
          <p className="mt-1 text-sm text-zinc-600 dark:text-zinc-400">
            Placeholder success path for the sample Saga definition.
          </p>
        </div>
        <span className="rounded-full border border-zinc-200 bg-zinc-50 px-3 py-1 text-xs font-medium text-zinc-600 dark:border-zinc-800 dark:bg-zinc-900 dark:text-zinc-400">
          YAML
        </span>
      </div>

      <div className="grid gap-3">
        {["reserve-inventory", "authorize-payment", "create-shipment"].map(
          (step, index) => (
            <div
              key={step}
              className="flex items-center gap-3 rounded-xl border border-zinc-200 bg-zinc-50 p-3 dark:border-zinc-800 dark:bg-zinc-900/70"
            >
              <span className="inline-flex size-7 shrink-0 items-center justify-center rounded-lg bg-emerald-500/10 text-xs font-semibold text-emerald-700 ring-1 ring-emerald-500/30 dark:text-emerald-300">
                {index + 1}
              </span>
              <span className="font-mono text-sm text-zinc-700 dark:text-zinc-300">
                {step}
              </span>
            </div>
          ),
        )}
      </div>
    </article>
  );
}
