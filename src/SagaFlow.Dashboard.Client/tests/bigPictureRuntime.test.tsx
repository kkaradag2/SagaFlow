import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, afterEach, expect, it, vi } from "vitest";
import { getDefinitionGraph, getInstanceRelationships, type InstanceContext, type InstanceRelationshipPage, type DefinitionGraph } from "../src/api/sagaflowApi";
import { BigPicturePage } from "../src/pages/BigPicturePage";
import { useInstanceRelationships } from "../src/live/useInstanceRelationships";
import { instanceContextFromSearch, instanceContextQuery } from "../src/layout/bigPictureRoute";

vi.mock("../src/api/sagaflowApi", async original => ({ ...await original<typeof import("../src/api/sagaflowApi")>(),
  getDefinitionGraph: vi.fn(), getInstanceRelationships: vi.fn() }));
let notify: (id: string | null) => void;
let subscribed = false;
vi.mock("../src/live/useSagaFlowLiveUpdates", () => ({ useSagaFlowLiveUpdates: (callback: typeof notify, enabled: boolean) => { notify = callback; subscribed = enabled; } }));
vi.mock("../src/components/BigPicture/BigPictureGraph", () => ({ default: ({ runtime }: { runtime: { invocationId: string }[] }) =>
  <div role="img" aria-label="Runtime graph">{runtime.map(row => <span key={row.invocationId}>{row.invocationId}</span>)}</div> }));
const context: InstanceContext = { instanceId: "local-1", domain: "order", process: "order-process", version: "1.0" };
const definition = { domain: context.domain, name: context.process, version: context.version, steps: [],
  bigPicture: [{ domain: "return", process: "return-process", version: "2.0", relationship: "invokes" as const }] };
const graph: DefinitionGraph = { nodes: [{ domain: "order", process: "order-process", version: "1.0", origin: "Local" }],
  edges: [{ source: { domain: "order", name: "order-process", version: "1.0" }, targetDomain: "return", targetProcess: "return-process",
    requestedVersion: "2.0", target: null, relationship: "invokes" }], failures: [] };
function page(id = "local-1", invocation = "invocation-1", nextOffset: number | null = null): InstanceRelationshipPage {
  return { localInstanceId: id, nextOffset, items: [{ invocationId: invocation, perspective: "Source", source: context,
    sourceSagaInstanceId: id, sourceStep: "invoke", sourceCorrelationId: "source-correlation", target: { domain: "return", process: "return-process", version: "2.0" },
    targetSagaInstanceId: null, targetCorrelationId: null, lastKnownStatus: "Prepared", targetLocalStatus: null,
    createdAt: null, updatedAt: null, lastObservedAt: null, reconciliation: null }] };
}
beforeEach(() => { vi.clearAllMocks(); vi.mocked(getDefinitionGraph).mockResolvedValue(graph); vi.mocked(getInstanceRelationships).mockResolvedValue(page()); });
afterEach(() => vi.unstubAllGlobals());
function Watch({ value }: { value: InstanceContext | null }) {
  const state = useInstanceRelationships(value);
  return <div>{state.items.map(row => <span key={row.invocationId}>{row.invocationId}</span>)}
    {state.loading && <p>Loading</p>}{state.error && <p role="alert">{state.error}</p>}
    <button onClick={state.loadMore}>More</button><button onClick={state.refresh}>Refresh runtime</button></div>;
}
it("keeps definition-only invokes view without runtime queries or subscription", async () => {
  render(<BigPicturePage definitions={[definition]} refreshIntervalMs={0} />);
  expect(await screen.findByRole("img")).toBeTruthy();
  expect(screen.getByText(/Definition View/)).toBeTruthy();
  expect(getInstanceRelationships).not.toHaveBeenCalled(); expect(subscribed).toBe(false);
  expect(getDefinitionGraph).toHaveBeenCalledWith(definition, expect.any(AbortSignal));
});
it("loads only the selected local instance, pages sequentially, and marks partial counts", async () => {
  const user = userEvent.setup();
  vi.mocked(getInstanceRelationships).mockResolvedValueOnce(page("local-1", "first", 100)).mockResolvedValueOnce(page("local-1", "second"));
  render(<BigPicturePage definitions={[definition]} instanceContext={context} refreshIntervalMs={0} />);
  expect(await screen.findByText(/Partial results: 1 loaded/)).toBeTruthy();
  expect(getInstanceRelationships).toHaveBeenCalledTimes(1);
  await user.click(screen.getByRole("button", { name: "Load more relationships" }));
  expect(await screen.findByText("second")).toBeTruthy(); expect(screen.getByText("first")).toBeTruthy();
  expect(getInstanceRelationships).toHaveBeenLastCalledWith(context, 100, expect.any(AbortSignal));
  expect(screen.queryByText(/Partial results/)).toBeNull();
});
it("isolates late responses when instance context changes and aborts on cleanup", async () => {
  let resolve!: (page: InstanceRelationshipPage) => void;
  vi.mocked(getInstanceRelationships).mockImplementationOnce(() => new Promise(r => { resolve = r; })).mockResolvedValueOnce(page("local-2", "new-invocation"));
  const view = render(<Watch value={context} />);
  const firstSignal = vi.mocked(getInstanceRelationships).mock.calls[0][2]!;
  view.rerender(<Watch value={{ ...context, instanceId: "local-2" }} />);
  expect(await screen.findByText("new-invocation")).toBeTruthy(); expect(firstSignal.aborted).toBe(true);
  await act(async () => resolve(page("local-1", "stale-invocation")));
  expect(screen.queryByText("stale-invocation")).toBeNull();
  view.unmount(); expect(vi.mocked(getInstanceRelationships).mock.calls[1][2]?.aborted).toBe(true);
});
it("refreshes only matching host-local notifications, deduplicates and resets pagination", async () => {
  render(<Watch value={context} />); await screen.findByText("invocation-1");
  await act(async () => notify("unrelated")); expect(getInstanceRelationships).toHaveBeenCalledTimes(1);
  await act(async () => notify("local-1")); expect(getInstanceRelationships).toHaveBeenCalledTimes(2);
  await act(async () => notify(null)); expect(getInstanceRelationships).toHaveBeenCalledTimes(3);
  expect(screen.getAllByText("invocation-1")).toHaveLength(1);
  expect(vi.mocked(getInstanceRelationships).mock.calls.every(call => call[1] === 0)).toBe(true);
});
it("coalesces notifications during an in-flight request without concurrent page fanout", async () => {
  let resolve!: (value: InstanceRelationshipPage) => void;
  vi.mocked(getInstanceRelationships).mockImplementationOnce(() => new Promise(r => { resolve = r; }));
  render(<Watch value={context} />);
  await act(async () => { notify("local-1"); notify("local-1"); }); expect(getInstanceRelationships).toHaveBeenCalledTimes(1);
  await act(async () => resolve(page()));
  await waitFor(() => expect(getInstanceRelationships).toHaveBeenCalledTimes(2));
  expect(screen.getAllByText("invocation-1")).toHaveLength(1);
});
it("shows errors and empty results, supports retry, and drops obsolete highlights", async () => {
  const user = userEvent.setup();
  vi.mocked(getInstanceRelationships).mockRejectedValueOnce(new Error("SagaFlow request failed (404)."))
    .mockResolvedValueOnce({ localInstanceId: "local-1", items: [], nextOffset: null });
  render(<BigPicturePage definitions={[definition]} instanceContext={context} refreshIntervalMs={0} />);
  expect(await screen.findByRole("alert")).toHaveProperty("textContent", expect.stringContaining("404"));
  await user.click(screen.getByRole("button", { name: "Retry instance relationships" }));
  expect(await screen.findByText("No runtime relationships recorded for this local instance.")).toBeTruthy();
});
it("clears cached relationships on failed refresh instead of displaying an obsolete status", async () => {
  render(<Watch value={context} />); await screen.findByText("invocation-1");
  vi.mocked(getInstanceRelationships).mockRejectedValueOnce(new Error("SagaFlow request failed (403)."));
  await act(async () => notify("local-1"));
  expect(await screen.findByRole("alert")).toHaveProperty("textContent", "SagaFlow request failed (403).");
  expect(screen.queryByText("invocation-1")).toBeNull();
});
it("rejects wrong-instance responses and invalid continuation offsets", async () => {
  vi.mocked(getInstanceRelationships).mockResolvedValueOnce(page("other-local"));
  const view = render(<Watch value={context} />);
  expect(await screen.findByRole("alert")).toHaveProperty("textContent", "Invalid instance relationship page.");
  vi.mocked(getInstanceRelationships).mockResolvedValueOnce(page("local-1", "invocation-1", 0));
  await userEvent.click(screen.getByRole("button", { name: "Refresh runtime" }));
  expect(await screen.findByRole("alert")).toBeTruthy(); view.unmount();
});
it("encodes deep-link context and calls the real client with the bounded S2-6A contract", async () => {
  expect(instanceContextFromSearch(instanceContextQuery(context))).toEqual(context);
  expect(instanceContextFromSearch("?instanceId=local-1")).toBeNull();
  const fetch = vi.fn().mockResolvedValue({ ok: true, json: async () => page() }); vi.stubGlobal("fetch", fetch);
  const actual = await vi.importActual<typeof import("../src/api/sagaflowApi")>("../src/api/sagaflowApi");
  await actual.getInstanceRelationships({ ...context, instanceId: "local/id" }, 100);
  expect(fetch).toHaveBeenCalledWith("api/instances/local%2Fid/relationships?domain=order&process=order-process&version=1.0&offset=100&limit=100", expect.anything());
});
