import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import definitions from "./fixtures/order-definition.json";
import WorkflowGraph from "../src/components/Workflow/WorkflowGraph";
import { layoutWorkflow } from "../src/components/Workflow/workflowLayout";
import { createWorkflowGraph, type WorkflowEdge, type WorkflowNode } from "../src/components/Workflow/workflowMapping";
import { ThemeProvider } from "../src/theme/ThemeProvider";

vi.mock("elkjs/lib/elk-api", () => ({ default: class { terminateWorker() {} } }));
vi.mock("../src/components/Workflow/workflowLayout", () => ({ layoutWorkflow: vi.fn() }));
vi.mock("@xyflow/react", async importOriginal => {
  const actual = await importOriginal<typeof import("@xyflow/react")>();
  const { useState } = await import("react");
  return { ...actual, Background: () => null, Controls: () => null,
    // A stateful canvas stand-in detects remounts/viewport resets caused by opening or closing the real drawer.
    ReactFlow: ({ nodes, edges, onNodeClick, fitView }: { nodes: WorkflowNode[]; edges: WorkflowEdge[]; onNodeClick: (event: unknown, node: WorkflowNode) => void; fitView: boolean }) => {
      const [zoom, setZoom] = useState(1);
      return <div data-testid="flow" data-zoom={zoom} data-fit-view={String(fitView)} data-failure-edges={edges.filter(edge => edge.data?.event === "failure").length}>
        <button type="button" onClick={() => setZoom(value => value + 1)}>Zoom canvas</button>
        {nodes.map(node => <button type="button" key={node.id} aria-label={`Select ${node.id}`} onClick={event => onNodeClick(event, node)}>{node.data.name}</button>)}
      </div>;
    },
  };
});

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(layoutWorkflow).mockImplementation(async graph => graph);
});

async function openGraph(definition = definitions[0]) {
  const result = render(<ThemeProvider><WorkflowGraph definition={definition} /></ThemeProvider>);
  await screen.findByRole("button", { name: "Select step:payment" });
  return result;
}

describe("workflow details drawer", () => {
  it("starts closed with Fit View and failure paths disabled", async () => {
    await openGraph();
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByTestId("flow").getAttribute("data-fit-view")).toBe("true");
    expect((screen.getByRole("checkbox", { name: "Show Failure Paths" }) as HTMLInputElement).checked).toBe(false);
    expect(screen.getByTestId("flow").getAttribute("data-failure-edges")).toBe("0");
    expect(screen.queryByText(/^Read-only ·/)).toBeNull();
    expect(screen.queryByText("TRUE", { exact: true })).toBeNull();
    expect(screen.queryByText("DEFAULT", { exact: true })).toBeNull();
  });

  it("opens on a step and closes with X without remounting the canvas or repeating layout", async () => {
    const user = userEvent.setup();
    await openGraph();
    const canvas = screen.getByTestId("flow");
    await user.click(screen.getByRole("button", { name: "Zoom canvas" }));
    await user.click(screen.getByRole("button", { name: "Select step:payment" }));
    const drawer = screen.getByRole("dialog", { name: "Step details" });
    expect(screen.getByTestId("workflow-canvas").contains(drawer)).toBe(false);
    expect(document.body.contains(drawer)).toBe(true);
    expect(within(drawer).getByText("AuthorizePayment")).toBeTruthy();
    expect(within(drawer).getByText("RefundPayment")).toBeTruthy();
    expect(within(drawer).getByText("Timeout")).toBeTruthy();
    expect(document.activeElement).toBe(within(drawer).getByRole("button", { name: "Close node details" }));
    await user.click(within(drawer).getByRole("button", { name: "Close node details" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByTestId("flow")).toBe(canvas);
    expect(canvas.getAttribute("data-zoom")).toBe("2");
    expect(layoutWorkflow).toHaveBeenCalledTimes(1);
  });

  it("updates the same open drawer when a condition node is selected", async () => {
    const user = userEvent.setup();
    await openGraph();
    await user.click(screen.getByRole("button", { name: "Select step:inventory" }));
    const drawer = screen.getByRole("dialog");
    drawer.querySelector<HTMLDivElement>(".overflow-y-auto")!.scrollTop = 200;
    await user.click(screen.getByRole("button", { name: "Select condition:inventory:success:0" }));
    expect(screen.getByRole("dialog", { name: "Condition details" })).toBe(drawer);
    expect(drawer.querySelector<HTMLDivElement>(".overflow-y-auto")!.scrollTop).toBe(0);
    expect(within(drawer).getByText("context.totalQuantity")).toBeTruthy();
    expect(within(drawer).getByText("greaterThan (>)")).toBeTruthy();
    expect(within(drawer).getByText("1000")).toBeTruthy();
    expect(within(drawer).getByText("TRUE → managerNotification")).toBeTruthy();
    expect(within(drawer).getByText("FALSE → shipment")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Select condition:managerApproval:success:0" }));
    expect(screen.getByRole("dialog")).toBe(drawer);
    expect(within(drawer).getByText("steps.managerApproval.result.approved")).toBeTruthy();
    expect(within(drawer).getByText("TRUE → shipment")).toBeTruthy();
    expect(within(drawer).getByText("FALSE → compensation")).toBeTruthy();
    expect(layoutWorkflow).toHaveBeenCalledTimes(1);
  });

  it("closes with Escape while preserving the existing viewport instance", async () => {
    const user = userEvent.setup();
    await openGraph();
    const canvas = screen.getByTestId("flow");
    await user.click(screen.getByRole("button", { name: "Zoom canvas" }));
    await user.click(screen.getByRole("button", { name: "Select condition:inventory:success:0" }));
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByTestId("flow")).toBe(canvas);
    expect(canvas.getAttribute("data-zoom")).toBe("2");
    expect(layoutWorkflow).toHaveBeenCalledTimes(1);
  });

  it("shows technical failures only after the user enables them", async () => {
    const user = userEvent.setup();
    await openGraph();
    await user.click(screen.getByRole("checkbox", { name: "Show Failure Paths" }));
    await waitFor(() => expect(screen.getByTestId("flow").getAttribute("data-failure-edges")).toBe("6"));
    await user.click(screen.getByRole("checkbox", { name: "Show Failure Paths" }));
    await waitFor(() => expect(screen.getByTestId("flow").getAttribute("data-failure-edges")).toBe("0"));
  });

  it("shows the next condition before fallback and does not invent missing condition metadata", async () => {
    const definition = structuredClone(definitions[0]);
    const step = definition.steps.find(step => step.name === "inventory")!;
    step.transitions.splice(1, 0, { event: "success", when: "result.priority == true", condition: null, else: null, nextStep: "shipment" });
    const user = userEvent.setup();
    await openGraph(definition);
    await user.click(screen.getByRole("button", { name: "Select condition:inventory:success:0" }));
    const drawer = screen.getByRole("dialog");
    expect(within(drawer).getByText("Next rule when unmatched")).toBeTruthy();
    expect(within(drawer).getByText("result.priority == true")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Select condition:inventory:success:1" }));
    expect(within(drawer).getByText("Expression")).toBeTruthy();
    expect(within(drawer).queryByText("Path", { exact: true })).toBeNull();
    expect(within(drawer).queryByText("Operator", { exact: true })).toBeNull();
    expect(within(drawer).getByText("DEFAULT → shipment")).toBeTruthy();
    expect(createWorkflowGraph(definition).edges.some(edge => edge.source.startsWith("condition:inventory:") && edge.label === "FALSE")).toBe(false);
  });
});
