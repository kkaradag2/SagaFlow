import { fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import definitions from "./fixtures/order-definition.json";
import { getDefinitionGraph, getDefinitions, getInstances, type SagaDefinition } from "../src/api/sagaflowApi";
import { DashboardLayout } from "../src/layout/DashboardLayout";
import { ThemeProvider } from "../src/theme/ThemeProvider";
import { WorkflowNodeDetails } from "../src/components/Workflow/WorkflowNodeDetails";

vi.mock("../src/api/sagaflowApi", () => ({ getDefinitions: vi.fn(), getDefinitionGraph: vi.fn(), getInstances: vi.fn(), getInstance: vi.fn() }));
vi.mock("../src/live/useSagaFlowLiveUpdates", () => ({ useSagaFlowLiveUpdates: vi.fn() }));
// Navigation tests exercise real page state; graph geometry is covered separately with the real ELK implementation.
vi.mock("../src/components/Workflow/WorkflowGraph", () => ({ default: ({ definition }: { definition: SagaDefinition }) => <div role="img" aria-label={`${definition.name} workflow diagram`} /> }));
vi.mock("../src/components/BigPicture/BigPictureGraph", () => ({ default: ({ workflow, onWorkflowOpen, onWorkflowBack }: {
  workflow: { domain: string; process: string; version: string } | null;
  onWorkflowOpen: (workflow: { domain: string; process: string; version: string }) => void;
  onWorkflowBack: () => void;
}) => workflow ? <div role="region" aria-label="BigPicture workflow">
  <button onClick={onWorkflowBack}>Back to BigPicture</button><span>{`${workflow.domain}/${workflow.process}@${workflow.version}`}</span>
  </div> : <div role="img" aria-label="BigPicture definition graph">
    <button onClick={() => onWorkflowOpen({ domain: "order", process: "order-process", version: "1.0" })}>Open local process</button>
    <button onClick={() => onWorkflowOpen({ domain: "return", process: "return-process", version: "1.0" })}>Open remote process</button>
  </div> }));

const related: SagaDefinition = definitions[0] as SagaDefinition;
const withoutRelationship: SagaDefinition = { ...related, bigPicture: [] };
const relatedGraph = { nodes: [{ domain: "order", process: "order-process", version: "1.0", origin: "Local" as const }],
  edges: [{ source: { domain: "order", name: "order-process", version: "1.0" }, targetDomain: "return",
    targetProcess: "return-process", requestedVersion: "1.0", target: null, relationship: "related" as const }],
  failures: [{ source: { domain: "order", name: "order-process", version: "1.0" }, targetDomain: "return",
    targetProcess: "return-process", kind: "Unavailable", message: "Remote unavailable" }] };

beforeEach(() => {
  vi.resetAllMocks();
  window.history.replaceState(null, "", "/sagaflow/");
  vi.mocked(getDefinitions).mockResolvedValue([withoutRelationship]);
  vi.mocked(getDefinitionGraph).mockResolvedValue(relatedGraph);
  vi.mocked(getInstances).mockResolvedValue([]);
  vi.stubGlobal("matchMedia", vi.fn(() => ({ matches: false })));
});

afterEach(() => vi.unstubAllGlobals());

function openDashboard() {
  return render(<ThemeProvider><DashboardLayout /></ThemeProvider>);
}

describe("workflow navigation", () => {
  it("starts with one local workflow list, opens only the selected workflow, and returns via its breadcrumb", async () => {
    const user = userEvent.setup();
    const second: SagaDefinition = { ...definitions[0], name: "return-flow", domain: "returns", version: "2.0" };
    vi.mocked(getDefinitions).mockResolvedValue([withoutRelationship, second]);
    openDashboard();
    await user.click(await screen.findByRole("button", { name: /order-process/ }));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Expand navigation" }).getAttribute("aria-expanded")).toBe("false");
    await user.click(screen.getByRole("button", { name: "Expand navigation" }));
    expect(screen.getByRole("button", { name: "Collapse navigation" }).getAttribute("aria-expanded")).toBe("true");
    expect(screen.getByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(screen.queryByRole("img", { name: "return-flow workflow diagram" })).toBeNull();
    const breadcrumb = screen.getByRole("navigation", { name: "Workflow breadcrumb" });
    expect(within(breadcrumb).getByText("order-process")).toBeTruthy();
    await user.click(within(breadcrumb).getByRole("button", { name: "Workflows" }));
    expect(await screen.findByRole("button", { name: /return-flow/ })).toBeTruthy();
    expect(screen.queryByRole("img")).toBeNull();
  });

  it("closes mobile navigation on workflow selection without introducing a collapsed rail", async () => {
    vi.mocked(window.matchMedia).mockReturnValue({ matches: true } as MediaQueryList);
    const user = userEvent.setup();
    openDashboard();
    await user.click(screen.getByRole("button", { name: "Open navigation" }));
    expect(screen.getByRole("button", { name: "Close navigation backdrop" })).toBeTruthy();
    await user.click(await screen.findByRole("button", { name: /order-process/ }));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Close navigation backdrop" })).toBeNull();
    expect(screen.getByRole("button", { name: "Open navigation" }).getAttribute("aria-expanded")).toBe("false");
    expect(screen.queryByRole("button", { name: "Expand navigation" })).toBeNull();
  });

  it("keeps Overview and Instances accessible while hiding BigPicture without an explicit relationship", async () => {
    const user = userEvent.setup();
    const { container } = openDashboard();
    await screen.findByRole("button", { name: /order-process/ });
    const sidebar = within(container.querySelector("aside")!);
    expect(sidebar.getByRole("button", { name: "Workflows", exact: true })).toBeTruthy();
    expect(sidebar.getByRole("button", { name: "Instances", exact: true })).toBeTruthy();
    expect(sidebar.getByRole("button", { name: "Overview", exact: true })).toBeTruthy();
    expect(sidebar.queryByRole("button", { name: "BigPicture", exact: true })).toBeNull();
    await user.click(sidebar.getByRole("button", { name: "Overview", exact: true }));
    expect(await screen.findByText("Local Saga Runtime")).toBeTruthy();
    await user.click(sidebar.getByRole("button", { name: "Instances", exact: true }));
    expect(await screen.findByText("No saga instances.")).toBeTruthy();
    expect(getInstances).toHaveBeenCalled();
    await user.click(sidebar.getByRole("button", { name: "Workflows", exact: true }));
    expect(await screen.findByRole("button", { name: /order-process/ })).toBeTruthy();
  });

  it("shows load failures and supports retry without displaying a fake workflow", async () => {
    vi.mocked(getDefinitions).mockRejectedValueOnce(new Error("Definitions unavailable"));
    const user = userEvent.setup();
    openDashboard();
    expect((await screen.findByRole("alert")).textContent).toBe("Definitions unavailable");
    expect(screen.queryByRole("button", { name: /order-process/ })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Refresh workflows" }));
    expect(await screen.findByRole("button", { name: /order-process/ })).toBeTruthy();
  });

  it("keeps legacy references out of navigation and the workflow graph", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([{ ...definitions[0], bigPicture: [{ domain: "accounts", process: "reconciliation", version: "2.0", endpoint: "https://accounts.example" }] }]);
    const user = userEvent.setup();
    openDashboard();
    await user.click(await screen.findByRole("button", { name: /order-process/ }));
    await screen.findByRole("img", { name: "order-process workflow diagram" });
    expect(screen.queryByRole("button", { name: "BigPicture" })).toBeNull();
    expect(screen.queryByRole("img", { name: "BigPicture definition graph" })).toBeNull();
    expect(screen.queryByRole("link", { name: /reconciliation/ })).toBeNull();
  });

  it("shows a separate BigPicture page for an authorized relationship, even when the target is unresolved", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([related]);
    const user = userEvent.setup();
    openDashboard();
    await user.click(await screen.findByRole("button", { name: "BigPicture" }));
    expect(window.location.pathname).toBe("/sagaflow/bigpicture");
    expect(await screen.findByRole("img", { name: "BigPicture definition graph" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Workflows", exact: true }));
    await user.click(await screen.findByRole("button", { name: /order-process/ }));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(screen.queryByRole("img", { name: "BigPicture definition graph" })).toBeNull();
  });

  it("uses URL history for local and remote workflows, direct routes, and return navigation", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([related]);
    const user = userEvent.setup();
    openDashboard();
    await user.click(await screen.findByRole("button", { name: "BigPicture" }));
    await user.click(await screen.findByRole("button", { name: "Open local process" }));
    expect(window.location.pathname).toBe("/sagaflow/bigpicture/workflow/order/order-process/1.0");
    expect(screen.getByText("order/order-process@1.0")).toBeTruthy();
    window.history.replaceState(null, "", "/sagaflow/bigpicture");
    fireEvent.popState(window);
    expect(await screen.findByRole("button", { name: "Open remote process" })).toBeTruthy();
    window.history.replaceState(null, "", "/sagaflow/bigpicture/workflow/order/order-process/1.0");
    fireEvent.popState(window);
    expect(await screen.findByText("order/order-process@1.0")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Back to BigPicture" }));
    expect(window.location.pathname).toBe("/sagaflow/bigpicture");
    await user.click(await screen.findByRole("button", { name: "Open remote process" }));
    expect(window.location.pathname).toBe("/sagaflow/bigpicture/workflow/return/return-process/1.0");
    expect(screen.getByText("return/return-process@1.0")).toBeTruthy();
    expect(getInstances).not.toHaveBeenCalled();
    window.history.replaceState(null, "", "/sagaflow/bigpicture/workflow/return/return-process/1.0");
    fireEvent.popState(window);
    expect(await screen.findByText("return/return-process@1.0")).toBeTruthy();
  });

  it("does not reveal a redacted relationship or show a transient menu during loading", async () => {
    let resolve!: (value: SagaDefinition[]) => void;
    vi.mocked(getDefinitions).mockReturnValue(new Promise(done => { resolve = done; }));
    openDashboard();
    expect(screen.queryByRole("button", { name: "BigPicture" })).toBeNull();
    resolve([withoutRelationship]);
    await screen.findByRole("button", { name: /order-process/ });
    expect(screen.queryByRole("button", { name: "BigPicture" })).toBeNull();
  });

  it("redirects direct BigPicture navigation when metadata is unavailable", async () => {
    window.history.replaceState(null, "", "/sagaflow/bigpicture");
    openDashboard();
    expect(screen.queryByRole("button", { name: "BigPicture" })).toBeNull();
    await screen.findByRole("button", { name: /order-process/ });
    expect(window.location.pathname).toBe("/sagaflow/workflows");
    expect(screen.queryByRole("img", { name: "BigPicture definition graph" })).toBeNull();
  });

  it("shows actual node metadata and optional fields without inventing a timeout", () => {
    const step = definitions[0].steps.find(step => step.name === "managerApproval")!;
    render(<WorkflowNodeDetails node={{ kind: "step", name: step.name, step, incoming: [], outgoing: [] }} />);
    expect(screen.getByText("RequestManagerApproval")).toBeTruthy();
    expect(screen.getByText("steps.managerApproval.result.approved = true")).toBeTruthy();
    expect(screen.getAllByText("Default (unconditional fallback)")).toHaveLength(2);
    expect(screen.queryByText("Timeout", { exact: true })).toBeNull();
    expect(screen.queryByText("Compensation command")).toBeNull();
    expect(screen.getByText("Max attempts (metadata)")).toBeTruthy();
  });
});
