import { render, screen, within } from "@testing-library/react";
import type { ComponentType } from "react";
import type { NodeProps } from "@xyflow/react";
import type { ProcessNode, ProcessEdge } from "../src/components/BigPicture/bigPictureMapping";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import definitions from "./fixtures/order-definition.json";
import { getDefinitionGraph, getDefinitions, getInstances, getInstance, getInstanceRelationships, type DefinitionGraph, type SagaDefinition, type SagaInstanceDetail } from "../src/api/sagaflowApi";
import { DashboardLayout } from "../src/layout/DashboardLayout";
import { ThemeProvider } from "../src/theme/ThemeProvider";
import { WorkflowNodeDetails } from "../src/components/Workflow/WorkflowNodeDetails";

vi.mock("../src/api/sagaflowApi", () => ({ getDefinitions: vi.fn(), getDefinitionGraph: vi.fn(), getInstances: vi.fn(), getInstance: vi.fn(), getInstanceRelationships: vi.fn() }));
vi.mock("../src/live/useSagaFlowLiveUpdates", () => ({ useSagaFlowLiveUpdates: vi.fn() }));
// Navigation tests exercise real page state; graph geometry is covered separately with the real ELK implementation.
vi.mock("../src/components/Workflow/WorkflowGraph", () => ({ default: ({ definition }: { definition: SagaDefinition }) => <div role="img" aria-label={`${definition.name} workflow diagram`} /> }));
vi.mock("@xyflow/react", async importOriginal => {
  const actual = await importOriginal<typeof import("@xyflow/react")>();
  return { ...actual, Background: () => null, Controls: () => null, Handle: () => null,
    ReactFlow: ({ nodes, edges, nodeTypes: { process: ProcessNodeCard } }: {
      nodes: ProcessNode[]; edges: ProcessEdge[]; nodeTypes: { process: ComponentType<NodeProps<ProcessNode>> };
    }) => <div>
      {nodes.map(node => <ProcessNodeCard key={node.id} data={node.data} id={node.id} type="process" selected={false}
        dragging={false} isConnectable={false} zIndex={0} selectable={false} deletable={false} draggable={false} />)}
      {edges.map(edge => <span key={edge.id}>{edge.label}</span>)}
    </div>,
  };
});

const related: SagaDefinition = definitions[0] as SagaDefinition;
const withoutRelationship: SagaDefinition = { ...related, bigPicture: [] };
const relatedGraph: DefinitionGraph = { nodes: [{ domain: "order", process: "order-process", version: "1.0", origin: "Local" as const }],
  edges: [{ source: { domain: "order", name: "order-process", version: "1.0" }, targetDomain: "return",
    targetProcess: "return-process", requestedVersion: null, target: null, relationship: "related" as const }],
  failures: [{ source: { domain: "order", name: "order-process", version: "1.0" }, targetDomain: "return",
    targetProcess: "return-process", kind: "Unavailable", message: "Remote unavailable" }] };

const remote: SagaDefinition = { ...withoutRelationship, domain: "return", name: "return-process", version: "1.0" };
const resolvedGraph: DefinitionGraph = {
  nodes: [...relatedGraph.nodes, { domain: remote.domain, process: remote.name, version: remote.version, origin: "Remote", definition: remote }],
  edges: [{ ...relatedGraph.edges[0], target: { domain: remote.domain, name: remote.name, version: remote.version } }], failures: [],
};
const topology = () => screen.getByRole("region", { name: "Process Explorer topology" });
const breadcrumb = () => within(screen.getByRole("navigation", { name: "Breadcrumb" }));
const localWorkflow = { name: /^Open workflow for order \/ order-process/ };
const remoteWorkflow = { name: /^Open workflow for return \/ return-process/ };

beforeEach(() => {
  vi.resetAllMocks();
  window.history.replaceState(null, "", "/sagaflow/");
  vi.mocked(getDefinitions).mockResolvedValue([withoutRelationship]);
  vi.mocked(getDefinitionGraph).mockResolvedValue(relatedGraph);
  vi.mocked(getInstances).mockResolvedValue([]);
  vi.mocked(getInstanceRelationships).mockResolvedValue({ localInstanceId: "local-saga", items: [], nextOffset: null });
  vi.stubGlobal("matchMedia", vi.fn(() => ({ matches: false })));
});

afterEach(() => vi.unstubAllGlobals());

function openDashboard() {
  return render(<ThemeProvider><DashboardLayout /></ThemeProvider>);
}

describe("workflow navigation", () => {
  it("keeps instance context through workflow, topology, and local instance navigation", async () => {
    const user = userEvent.setup();
    const time = "2026-11-05T10:00:00Z";
    const instance: SagaInstanceDetail = { id: "local-saga", processName: related.name, definitionDomain: related.domain,
      definitionVersion: related.version, correlationId: "local-correlation", status: "Running", currentStep: null,
      createdAt: time, updatedAt: time, failureReason: null, incomingInvocation: null, steps: [], compensations: [], domainRollback: null,
      domainStarter: { operationName: "Start", rollbackOperationName: "Rollback", status: null, startedAt: null, completedAt: null, input: null, result: null } };
    vi.mocked(getDefinitions).mockResolvedValue([{ ...related, bigPicture: [{ domain: "return", process: "return-process", version: "1.0", relationship: "invokes" }] }]);
    vi.mocked(getDefinitionGraph).mockResolvedValue({ ...resolvedGraph, edges: [{ ...resolvedGraph.edges[0], relationship: "invokes" }] });
    vi.mocked(getInstanceRelationships).mockResolvedValue({ localInstanceId: instance.id, nextOffset: null, items: [{
      invocationId: "invocation-1", perspective: "Source", source: { domain: related.domain, process: related.name, version: related.version },
      sourceSagaInstanceId: instance.id, sourceStep: "invoke", sourceCorrelationId: instance.correlationId,
      target: { domain: remote.domain, process: remote.name, version: remote.version }, targetSagaInstanceId: null, targetCorrelationId: null,
      lastKnownStatus: "Accepted", targetLocalStatus: null, createdAt: null, updatedAt: null, lastObservedAt: null, reconciliation: null,
    }] });
    vi.mocked(getInstance).mockResolvedValue(instance); vi.mocked(getInstances).mockResolvedValue([instance]);
    window.history.replaceState(null, "", "/sagaflow/instances?instanceId=local-saga");
    openDashboard();
    await user.click(await screen.findByRole("button", { name: "View instance in Process Explorer" }));
    const contextUrl = window.location.pathname + window.location.search;
    expect(contextUrl).toContain("/bigpicture?instanceId=local-saga");
    expect(window.location.search).toContain("process=order-process");
    expect(getInstanceRelationships).toHaveBeenCalledWith(expect.objectContaining({ instanceId: instance.id, domain: related.domain, version: related.version }), 0, expect.any(AbortSignal));
    await user.click(await screen.findByRole("button", remoteWorkflow));
    expect(await screen.findByRole("img", { name: "return-process workflow diagram" })).toBeTruthy();
    expect(window.location.pathname).toBe("/sagaflow/bigpicture/workflow/return/return-process/1.0");
    expect(window.location.search).toContain("instanceId=local-saga");
    expect(breadcrumb().getByText("Return", { exact: true }).getAttribute("aria-current")).toBe("page");
    const parent = breadcrumb().getByRole("link", { name: "Process Explorer" });
    expect(parent.getAttribute("href")).toBe(contextUrl);
    await user.click(parent);
    expect(window.location.pathname + window.location.search).toBe(contextUrl);
    expect(breadcrumb().getByText("Process Explorer").getAttribute("aria-current")).toBe("page");
    await user.click(within(screen.getByRole("region", { name: "Loaded runtime relationships" })).getByRole("button"));
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Open local instance" }));
    expect(await screen.findByRole("button", { name: "View instance in Process Explorer" })).toBeTruthy();
    expect(window.location.pathname + window.location.search).toBe("/sagaflow/instances?instanceId=local-saga");
    window.history.back();
    expect(await screen.findByRole("region", { name: "Instance context" })).toBeTruthy();
    window.history.forward();
    expect(await screen.findByRole("button", { name: "View instance in Process Explorer" })).toBeTruthy();
  });

  it("shows a standalone Local node, opens its workflow, and returns to topology", async () => {
    const user = userEvent.setup();
    openDashboard();
    await screen.findByRole("region", { name: "Process Explorer topology" });
    expect(breadcrumb().getByText("Dashboard", { exact: true })).toBeTruthy();
    expect(breadcrumb().getByText("Process Explorer", { exact: true }).getAttribute("aria-current")).toBe("page");
    expect(breadcrumb().queryByRole("link")).toBeNull();
    expect(within(topology()).getByText("Local", { exact: true })).toBeTruthy();
    expect(within(topology()).queryByText("Remote", { exact: true })).toBeNull();
    expect(getDefinitionGraph).not.toHaveBeenCalled();
    await user.click(within(topology()).getByRole("button", localWorkflow));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(window.location.pathname).toBe("/sagaflow/bigpicture/workflow/order/order-process/1.0");
    expect(breadcrumb().getByText("Order", { exact: true }).getAttribute("aria-current")).toBe("page");
    expect(breadcrumb().getByRole("link", { name: "Process Explorer" })).toBeTruthy();
    expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Process Explorer");
    expect(screen.getByRole("button", { name: "Expand navigation" }).getAttribute("aria-expanded")).toBe("false");
    await user.click(screen.getByRole("button", { name: "Expand navigation" }));
    expect(screen.getByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Back to Topology" }));
    expect(within(topology()).getByRole("button", localWorkflow)).toBeTruthy();
    expect(window.location.pathname).toBe("/sagaflow/bigpicture");
  });

  it("keeps one Process Explorer sidebar entry alongside Overview and Instances", async () => {
    const user = userEvent.setup();
    const { container } = openDashboard();
    const sidebar = within(container.querySelector("aside")!);
    expect(sidebar.getAllByRole("button", { name: "Process Explorer", exact: true })).toHaveLength(1);
    expect(sidebar.queryByRole("button", { name: /^(Workflows?|BigPicture)$/ })).toBeNull();
    await user.click(sidebar.getByRole("button", { name: "Overview", exact: true }));
    expect(await screen.findByText("Local Saga Runtime")).toBeTruthy();
    await user.click(sidebar.getByRole("button", { name: "Instances", exact: true }));
    expect(await screen.findByText("No saga instances.")).toBeTruthy();
    await user.click(sidebar.getByRole("button", { name: "Process Explorer", exact: true }));
    expect(await screen.findByRole("button", localWorkflow)).toBeTruthy();
  });

  it("shows definition load failures and retries without displaying a fake workflow", async () => {
    vi.mocked(getDefinitions).mockRejectedValueOnce(new Error("Definitions unavailable"));
    const user = userEvent.setup();
    openDashboard();
    expect((await screen.findByRole("alert")).textContent).toBe("Definitions unavailable");
    expect(screen.queryByRole("button", localWorkflow)).toBeNull();
    await user.click(screen.getByRole("button", { name: "Retry loading definitions" }));
    expect(await screen.findByRole("button", localWorkflow)).toBeTruthy();
  });

  it("renders declared topology, opens published remote workflows, and preserves deep links", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([related]);
    vi.mocked(getDefinitionGraph).mockResolvedValue(resolvedGraph);
    const user = userEvent.setup();
    window.history.replaceState(null, "", "/custom/dashboard/bigpicture");
    openDashboard();
    await screen.findByRole("region", { name: "Process Explorer topology" });
    expect(within(topology()).getByText("related", { exact: true })).toBeTruthy();
    expect(within(topology()).getByText("Remote", { exact: true })).toBeTruthy();
    expect(getDefinitionGraph).toHaveBeenCalledWith(related, expect.any(AbortSignal));
    await user.click(within(topology()).getByRole("button", remoteWorkflow));
    expect(await screen.findByRole("img", { name: "return-process workflow diagram" })).toBeTruthy();
    const deepLink = window.location.pathname;
    expect(deepLink).toBe("/custom/dashboard/bigpicture/workflow/return/return-process/1.0");
    expect(breadcrumb().getByText("Return", { exact: true }).getAttribute("aria-current")).toBe("page");
    window.history.back();
    expect((await breadcrumb().findByText("Process Explorer", { selector: '[aria-current="page"]' })).textContent).toBe("Process Explorer");
    expect(window.location.pathname).toBe("/custom/dashboard/bigpicture");
    window.history.forward();
    expect(await breadcrumb().findByText("Return", { selector: '[aria-current="page"]' })).toBeTruthy();
    expect(window.location.pathname).toBe(deepLink);
    expect(await screen.findByRole("img", { name: "return-process workflow diagram" })).toBeTruthy();
    expect(getInstances).not.toHaveBeenCalled();
  });

  it("derives the breadcrumb from the domain on a direct workflow URL", async () => {
    const definition = { ...withoutRelationship, domain: "supply-chain" };
    vi.mocked(getDefinitions).mockResolvedValue([definition]);
    window.history.replaceState(null, "", `/custom/dashboard/bigpicture/workflow/${definition.domain}/${definition.name}/${definition.version}`);
    openDashboard();
    expect(breadcrumb().getByText("Supply-chain", { exact: true }).getAttribute("aria-current")).toBe("page");
    expect(breadcrumb().getByRole("link", { name: "Process Explorer" }).getAttribute("href")).toBe("/custom/dashboard/bigpicture");
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
  });

  it("uses separate keyboard-accessible node details and shows unresolved workflows as unavailable", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([related]);
    const user = userEvent.setup();
    openDashboard();
    const info = await screen.findByRole("button", { name: /^View details for return \/ return-process/ });
    info.focus();
    await user.keyboard("{Enter}");
    const drawer = screen.getByRole("dialog", { name: "BigPicture details" });
    expect(within(drawer).getByText("Unresolved")).toBeTruthy();
    expect(within(drawer).getByText("Remote", { exact: true })).toBeTruthy();
    expect(window.location.pathname).toBe("/sagaflow/");
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(document.activeElement).toBe(info);
    await user.click(screen.getByRole("button", remoteWorkflow));
    expect(window.location.pathname).toBe("/sagaflow/bigpicture/workflow/return/return-process");
    expect(screen.getByText(/Workflow definition unavailable/)).toBeTruthy();
    expect(screen.queryByRole("img", { name: /workflow diagram/ })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Back to Topology" }));
    expect(within(topology()).getByRole("button", remoteWorkflow)).toBeTruthy();
  });

  it("opens the Local workflow when all configured graph discovery requests fail", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([related]);
    vi.mocked(getDefinitionGraph).mockRejectedValue(new Error("SagaFlow request failed (503)."));
    const user = userEvent.setup();
    openDashboard();
    expect((await screen.findByRole("alert")).textContent).toContain("Unable to load or refresh process relationships");
    expect(within(topology()).getByText("Local", { exact: true })).toBeTruthy();
    expect(within(topology()).queryByRole("button", remoteWorkflow)).toBeNull();
    await user.click(within(topology()).getByRole("button", localWorkflow));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(screen.getByRole("alert")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Back to Topology" }));
    expect(within(topology()).getByRole("button", localWorkflow)).toBeTruthy();
  });

  it("keeps Back to Topology available when the remote graph request fails on a direct link", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([related]);
    vi.mocked(getDefinitionGraph).mockRejectedValue(new Error("SagaFlow request failed (503)."));
    window.history.replaceState(null, "", "/sagaflow/bigpicture/workflow/return/return-process/1.0");
    const user = userEvent.setup();
    openDashboard();
    expect(await screen.findByText(/Workflow definition unavailable/)).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toContain("Unable to load or refresh process relationships");
    expect(screen.queryByRole("img", { name: /workflow diagram/ })).toBeNull();
    await user.click(screen.getByRole("button", { name: "Back to Topology" }));
    expect(window.location.pathname).toBe("/sagaflow/bigpicture");
  });

  it("shows an empty state when the host has no local definitions", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([]);
    openDashboard();
    expect(await screen.findByText("No process definitions available.")).toBeTruthy();
    expect(getDefinitionGraph).not.toHaveBeenCalled();
    expect(screen.queryByRole("region", { name: "Process Explorer topology" })).toBeNull();
  });

  it("supports the legacy workflows URL and closes mobile navigation when opening a workflow", async () => {
    vi.mocked(window.matchMedia).mockReturnValue({ matches: true } as MediaQueryList);
    window.history.replaceState(null, "", "/sagaflow/workflows");
    const user = userEvent.setup();
    openDashboard();
    await user.click(screen.getByRole("button", { name: "Open navigation" }));
    expect(screen.getByRole("button", { name: "Close navigation backdrop" })).toBeTruthy();
    await user.click(await screen.findByRole("button", localWorkflow));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Close navigation backdrop" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Expand navigation" })).toBeNull();
  });

  it("keeps legacy-only references out of topology discovery", async () => {
    vi.mocked(getDefinitions).mockResolvedValue([{ ...related, bigPicture: [{ domain: "accounts", process: "reconciliation", version: "2.0" }] }]);
    openDashboard();
    expect(await screen.findByRole("button", localWorkflow)).toBeTruthy();
    expect(getDefinitionGraph).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: /Open workflow for accounts/ })).toBeNull();
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
