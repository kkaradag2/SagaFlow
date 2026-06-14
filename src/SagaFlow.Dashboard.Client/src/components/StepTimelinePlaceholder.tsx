export function StepTimelinePlaceholder() {
  return (
    <article className="rounded-2xl border border-zinc-200 bg-white p-6 shadow-sm dark:border-zinc-800 dark:bg-zinc-950/40 dark:shadow-none">
      <h2 className="m-0 text-sm font-semibold text-zinc-950 dark:text-zinc-100">
        Step Timeline
      </h2>
      <ol className="mt-5 space-y-4">
        {[
          "Inventory reservation pending",
          "Payment authorization pending",
          "Shipment creation pending",
        ].map((item) => (
          <li key={item} className="flex gap-3">
            <span className="mt-1.5 size-2 shrink-0 rounded-full bg-emerald-500" />
            <span className="text-sm text-zinc-600 dark:text-zinc-400">
              {item}
            </span>
          </li>
        ))}
      </ol>
    </article>
  );
}
