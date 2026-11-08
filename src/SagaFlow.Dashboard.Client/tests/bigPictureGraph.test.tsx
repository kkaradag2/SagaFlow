import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { DefinitionGraph, SagaDefinition, InstanceRelationship } from "../src/api/sagaflowApi";
import BigPictureGraph from "../src/components/BigPicture/BigPictureGraph";
import { mapBigPicture } from "../src/components/BigPicture/bigPictureMapping";
import { ProcessNodeCard } from "../src/components/BigPicture/ProcessNodeCard";
import { ThemeProvider } from "../src/theme/ThemeProvider";
import { useState } from "react";
import { workflowFromPath, workflowPath, type WorkflowIdentity } from "../src/layout/bigPictureRoute";

vi.mock("../src/components/Workflow/WorkflowGraph", () => ({ default: ({ definition }: { definition: SagaDefinition }) =>
  <div role="img" aria-label={`${definition.name} workflow diagram`}>{definition.steps.map(step => step.name).join(", ")}</div> }));

vi.mock("@xyflow/react", async importOriginal => {
  const actual = await importOriginal<typeof import("@xyflow/react")>();
  return { ...actual, Background: () => null, Controls: () => null, Handle: () => null,
    ReactFlow: ({ nodes, edges, onEdgeClick }: {
      nodes: ReturnType<typeof mapBigPicture>["nodes"]; edges: ReturnType<typeof mapBigPicture>["edges"];
      onEdgeClick: (event: unknown, edge: unknown) => void;
    }) => <div data-testid="bigpicture-flow">
      {nodes.map(node => <div key={node.id}>
        <ProcessNodeCard data={node.data} selected={false} id={node.id} type="process" dragging={false}
          isConnectable={false} zIndex={0} selectable={true} deletable={false} draggable={false}
          /></div>)}
      {edges.map(edge => <button type="button" key={edge.id} onClick={event => onEdgeClick(event, edge)}>
        {edge.label ?? "Unlabelled reference"}</button>)}</div>,
  };
});

const source = { domain: "order", name: "order-process", version: "1.0" };
const target = { domain: "return", name: "return-process", version: "2.0" };
const graph: DefinitionGraph = {
  nodes: [{ domain: "order", process: "order-process", version: "1.0", origin: "Local" },
    { domain: "return", process: "return-process", version: "2.0", origin: "Remote" }],
  edges: [{ source, targetDomain: "return", targetProcess: "return-process", requestedVersion: "2.0",
    target, relationship: "related" }], failures: [],
};
const row: InstanceRelationship = { invocationId: "invocation-1", perspective: "Source", source: { domain: "order", process: "order-process", version: "1.0" },
  target: { domain: "return", process: "return-process", version: "2.0" }, sourceSagaInstanceId: "source-saga", sourceStep: "invoke",
  sourceCorrelationId: "source-correlation", targetSagaInstanceId: null, targetCorrelationId: null, lastKnownStatus: "Accepted", targetLocalStatus: null,
  createdAt: null, updatedAt: null, lastObservedAt: null, reconciliation: { reason: "Local failure requires review", requiredAt: "2026-11-05T10:00:00Z" } };
const instanceContext = { ...row.source, instanceId: "source-saga" };

describe("BigPicture graph", () => {
  it("distinguishes static related/invokes and highlights only exact runtime invokes matches", () => {
    const invokes = { ...graph.edges[0], relationship: "invokes" as const };
    const mapped = mapBigPicture({ ...graph, edges: [graph.edges[0], invokes,
      { ...invokes, requestedVersion: "9.0", target: null }] }, [], [row, { ...row, invocationId: "invocation-2" },
      { ...row, invocationId: "invocation-3", target: { ...row.target, process: "other-process" } }], true);
    expect(mapped.edges[0].style?.strokeDasharray).toBe("6 4");
    expect(mapped.edges[0].data?.runtime).toEqual([]);
    expect(mapped.edges[1].style?.strokeDasharray).toBeUndefined();
    expect(mapped.edges[1].source).toBe("order/order-process@1.0");
    expect(mapped.edges[1].target).toBe("return/return-process@2.0");
    expect(mapped.edges[1].label).toBe("invokes · 2+ loaded");
    expect(mapped.edges[1].data?.runtime?.map(r => r.invocationId)).toEqual(["invocation-1", "invocation-2"]);
    expect(mapped.edges[2].data?.runtime).toEqual([]);
    expect(mapped.nodes.some(n => n.data.process === "other-process")).toBe(false);
  });
  it("keeps unresolved definitions unresolved even with a matching runtime record", () => {
    const mapped = mapBigPicture({ ...graph, nodes: graph.nodes.slice(0, 1), edges: [{ ...graph.edges[0], target: null, relationship: "invokes" }] }, [], [row]);
    expect(mapped.nodes[1].data.status).toBe("Unresolved");
    expect(mapped.edges[0].data?.runtime).toHaveLength(1);
  });
  it("preserves a selected relationship drawer when discovery resolves its target", async () => {
    const user = userEvent.setup();
    const edge = { ...graph.edges[0], relationship: "invokes" as const };
    const view = render(<ThemeProvider><BigPictureGraph graph={{ ...graph, nodes: graph.nodes.slice(0, 1), edges: [{ ...edge, target: null }] }}
      instanceContext={instanceContext} runtime={[row]} /></ThemeProvider>);
    await user.click(screen.getByRole("button", { name: "invokes · 1 loaded" }));
    expect(screen.getByRole("dialog").textContent).toContain("Unresolved");
    view.rerender(<ThemeProvider><BigPictureGraph graph={{ ...graph, edges: [edge] }} instanceContext={instanceContext} runtime={[row]} /></ThemeProvider>);
    expect(screen.getByRole("dialog").textContent).toContain("Resolved");
    expect(screen.getByRole("dialog").textContent).toContain("invocation-1");
  });
  it("shows each source invocation, copies identity, preserves nulls and only navigates locally", async () => {
    const user = userEvent.setup(); const navigate = vi.fn(); const copy = vi.spyOn(navigator.clipboard, "writeText");
    render(<ThemeProvider><BigPictureGraph graph={{ ...graph, edges: [{ ...graph.edges[0], relationship: "invokes" }] }}
      instanceContext={instanceContext} runtime={[row, { ...row, invocationId: "invocation-2" }]} onLocalInstanceOpen={navigate} /></ThemeProvider>);
    await user.click(screen.getByRole("button", { name: "invokes · 2 loaded" }));
    const drawer = screen.getByRole("dialog");
    expect(within(drawer).getAllByRole("article")).toHaveLength(2);
    expect(within(drawer).getAllByText("Last Known Remote Status")).toHaveLength(2);
    expect(within(drawer).queryByText("Target Local Status")).toBeNull();
    expect(within(drawer).getAllByText("Accepted")).toHaveLength(2);
    expect(within(drawer).queryByText("Origin: External")).toBeNull();
    expect(within(drawer).getAllByRole("alert")).toHaveLength(2);
    expect(within(drawer).queryByRole("link")).toBeNull();
    expect(within(drawer).queryByRole("button", { name: "Copy Target Saga ID" })).toBeNull();
    await user.click(within(drawer).getAllByRole("button", { name: "Copy Invocation ID" })[0]);
    expect(copy).toHaveBeenCalledWith("invocation-1");
    await user.click(within(drawer).getAllByRole("button", { name: "Open local instance" })[0]);
    expect(navigate).toHaveBeenCalledWith("source-saga");
    expect(drawer.className).toContain("100vw"); expect(drawer.className).toContain("dark:bg-zinc-950");
    await user.keyboard("{Escape}"); expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.getByTestId("bigpicture-canvas")).toBeTruthy();
  });
  it("opens node details in instance mode and separates target local status and correlations", async () => {
    const user = userEvent.setup(); const navigate = vi.fn();
    const targetRow: InstanceRelationship = { ...row, perspective: "Target", targetSagaInstanceId: "target-saga",
      targetCorrelationId: "target-correlation", lastKnownStatus: null, targetLocalStatus: "Running", reconciliation: null };
    render(<ThemeProvider><BigPictureGraph graph={graph} instanceContext={{ ...row.target, instanceId: "target-saga" }} runtime={[targetRow]}
      onLocalInstanceOpen={navigate} /></ThemeProvider>);
    await user.click(within(screen.getByTestId("bigpicture-flow")).getByRole("button", { name: /View details for .*return-process/ }));
    const drawer = screen.getByRole("dialog");
    expect(within(drawer).getByText("Target Local Status")).toBeTruthy();
    const origin = within(drawer).getByText("Origin: External");
    expect(origin.className).toContain("text-zinc-600");
    expect(origin.className).toContain("dark:text-zinc-300");
    expect(origin.className).toContain("dark:border-zinc-700");
    expect(within(drawer).getByText("Running")).toBeTruthy();
    expect(within(drawer).getByText("source-correlation")).toBeTruthy();
    expect(within(drawer).getByText("target-correlation")).toBeTruthy();
    expect(within(drawer).queryByText("Last Known Remote Status")).toBeNull();
    await user.click(within(drawer).getByRole("button", { name: "Open local instance" })); expect(navigate).toHaveBeenCalledWith("target-saga");
  });
  it("does not mark a normal local Saga as external in its process drawer", async () => {
    const user = userEvent.setup();
    render(<ThemeProvider><BigPictureGraph graph={graph} instanceContext={instanceContext} runtime={[]} /></ThemeProvider>);
    await user.click(within(screen.getByTestId("bigpicture-flow")).getByRole("button", { name: /View details for .*order-process/ }));
    const drawer = screen.getByRole("dialog");
    expect(within(drawer).getByText("Local", { exact: true })).toBeTruthy();
    expect(within(drawer).queryByText("Origin: External")).toBeNull();
    expect(within(drawer).queryByRole("article")).toBeNull();
  });
  it("keeps unmatched runtime metadata selectable without inventing graph nodes", async () => {
    const user = userEvent.setup(); const other = { ...row, target: { ...row.target, process: "unknown-process" } };
    render(<ThemeProvider><BigPictureGraph graph={graph} runtime={[other]} instanceContext={instanceContext} /></ThemeProvider>);
    await user.click(screen.getByRole("button", { name: /No matching declared invokes edge/ }));
    expect(screen.getByRole("dialog").textContent).toContain("return/unknown-process@2.0");
    expect(screen.queryByRole("button", { name: /unknown-process version/ })).toBeNull();
  });
  it("refreshes an open runtime drawer without changing selection or stealing keyboard focus", async () => {
    const user = userEvent.setup();
    const view = render(<ThemeProvider><BigPictureGraph graph={graph} runtime={[row]} instanceContext={instanceContext} /></ThemeProvider>);
    await user.click(within(screen.getByRole("region", { name: "Loaded runtime relationships" })).getByRole("button"));
    const copy = within(screen.getByRole("dialog")).getByRole("button", { name: "Copy Invocation ID" });
    copy.focus();
    view.rerender(<ThemeProvider><BigPictureGraph graph={graph} runtime={[{ ...row, lastKnownStatus: "Completed" }]} instanceContext={instanceContext} /></ThemeProvider>);
    expect(within(screen.getByRole("dialog")).getByText("Completed")).toBeTruthy();
    expect(within(screen.getByRole("dialog")).getByRole("alert")).toBeTruthy();
    expect(document.activeElement).toBe(copy);
  });
  it("retains selected identity when a refresh resets pagination without displaying stale status", async () => {
    const user = userEvent.setup();
    const view = render(<ThemeProvider><BigPictureGraph graph={graph} runtime={[row]} instanceContext={instanceContext} /></ThemeProvider>);
    await user.click(within(screen.getByRole("region", { name: "Loaded runtime relationships" })).getByRole("button"));
    view.rerender(<ThemeProvider><BigPictureGraph graph={graph} runtime={[]} instanceContext={instanceContext} /></ThemeProvider>);
    const drawer = screen.getByRole("dialog");
    expect(within(drawer).getByText("invocation-1")).toBeTruthy();
    expect(within(drawer).getByText(/not in the currently loaded results/)).toBeTruthy();
    expect(within(drawer).queryByText("Accepted")).toBeNull();
  });
  it("opens the matching local and discovered remote workflows from process nodes", async () => {
    const workflow = (domain: string, name: string, version: string): SagaDefinition => ({ domain, name, version,
      bigPicture: [], steps: [{ name: `${domain}-step`, command: "Inspect", timeoutLimit: null,
        maxAttempts: 1, compensation: null, transitions: [] }] });
    const withDefinitions: DefinitionGraph = { ...graph, nodes: graph.nodes.map(node => ({ ...node,
      definition: workflow(node.domain, node.process, node.version) })) };
    const user = userEvent.setup();
    function Harness() {
      const [selected, setSelected] = useState<WorkflowIdentity | null>(null);
      return <BigPictureGraph graph={withDefinitions} workflow={selected} onWorkflowOpen={setSelected} onWorkflowBack={() => setSelected(null)} />;
    }
    render(<ThemeProvider><Harness /></ThemeProvider>);
    await user.click(screen.getByRole("button", { name: /Open workflow for .*order-process/ }));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toHaveProperty("textContent", "order-step");
    await user.click(screen.getByRole("button", { name: "Back to Topology" }));
    await user.click(screen.getByRole("button", { name: /Open workflow for .*return-process/ }));
    expect(await screen.findByRole("img", { name: "return-process workflow diagram" })).toHaveProperty("textContent", "return-step");
  });

  it("encodes and parses an exact workflow identity for direct routes", () => {
    const identity = { domain: "return", process: "return-process", version: "2.0 beta" };
    const path = workflowPath("/sagaflow", identity);
    expect(path).toBe("/sagaflow/bigpicture/workflow/return/return-process/2.0%20beta");
    expect(workflowFromPath(path)).toEqual(identity);
    expect(workflowFromPath(workflowPath("/sagaflow", { ...identity, version: null }))).toEqual({ ...identity, version: null });
    expect(workflowFromPath("/sagaflow/bigpicture/workflow/return/return-process/%ZZ")).toBeNull();
  });

  it.each([
    { domain: "missing", process: "return-process", version: "2.0" },
    { domain: "return", process: "missing", version: "2.0" },
    { domain: "return", process: "return-process", version: "9.9" },
    { domain: "return", process: "return-process", version: "2.0" },
  ])("does not invent a graph without a matching workflow definition for $domain/$process@$version", async workflow => {
    render(<ThemeProvider><BigPictureGraph graph={graph} workflow={workflow} /></ThemeProvider>);
    expect(screen.getByRole("status").textContent).toContain("Workflow definition unavailable");
    expect(screen.queryByRole("img", { name: /workflow diagram/ })).toBeNull();
  });

  it("keeps an open drawer and updates its discovery status when the graph refreshes", async () => {
    const unresolved: DefinitionGraph = { nodes: graph.nodes.slice(0, 1),
      edges: [{ ...graph.edges[0], target: null }], failures: [{ source, targetDomain: "return",
        targetProcess: "return-process", requestedVersion: "2.0", kind: "Unavailable", message: "Remote unavailable" }] };
    const user = userEvent.setup();
    const view = render(<ThemeProvider><BigPictureGraph graph={unresolved} /></ThemeProvider>);
    await user.click(screen.getByRole("button", { name: /View details for .*return-process/ }));
    expect(screen.getByRole("dialog").textContent).toContain("Unresolved");
    view.rerender(<ThemeProvider><BigPictureGraph graph={graph} /></ThemeProvider>);
    expect(screen.getByRole("dialog").textContent).toContain("Resolved");
    expect(screen.getByRole("dialog").textContent).not.toContain("Remote unavailable");
  });
  it("associates failure reasons with the exact source and requested version", () => {
    const otherSource = { domain: "warehouse", name: "warehouse-process", version: "1.0" };
    const unresolved = (from: typeof source, version: string | null) => ({ ...graph.edges[0], source: from,
      requestedVersion: version, target: null });
    const mapped = mapBigPicture({ nodes: graph.nodes.slice(0, 1), edges: [
      unresolved(source, "1.0"), unresolved(source, "2.0"), unresolved(otherSource, "2.0")],
      failures: [
        { source, targetDomain: "return", targetProcess: "return-process", requestedVersion: "1.0", kind: "NotFound", message: "First" },
        { source, targetDomain: "return", targetProcess: "return-process", requestedVersion: "2.0", kind: "Unavailable", message: "Second" },
        { source: otherSource, targetDomain: "return", targetProcess: "return-process", requestedVersion: "2.0", kind: "Timeout", message: "Third" }] });
    expect(mapped.edges.map(edge => edge.data?.failure)).toEqual(["NotFound: First", "Unavailable: Second", "Timeout: Third"]);
    expect(mapped.edges[1].target).not.toBe(mapped.edges[2].target);
    expect(mapped.nodes.filter(node => node.data.status === "Unresolved").map(node => node.data.failure))
      .toEqual(["NotFound: First", "Unavailable: Second", "Timeout: Third"]);
  });

  it("does not attach an ambiguous legacy failure to a versioned reference", () => {
    const mapped = mapBigPicture({ nodes: graph.nodes.slice(0, 1), edges: [
      { ...graph.edges[0], requestedVersion: "1.0", target: null },
      { ...graph.edges[0], requestedVersion: "2.0", target: null }],
      failures: [{ source, targetDomain: "return", targetProcess: "return-process", kind: "Unavailable", message: "Ambiguous" }] });
    expect(mapped.edges.map(edge => edge.data?.failure)).toEqual([undefined, undefined]);
  });
  it("keeps direction, separate references and only explicit labels", () => {
    const mapped = mapBigPicture({ ...graph, edges: [graph.edges[0], { ...graph.edges[0], relationship: null }] });
    expect(mapped.nodes.map(node => [node.data.domain, node.data.version, node.data.origin, node.data.status]))
      .toEqual([["order", "1.0", "Local", "Resolved"], ["return", "2.0", "Remote", "Resolved"]]);
    expect(mapped.edges.map(edge => [edge.source, edge.target, edge.label]))
      .toEqual([["order/order-process@1.0", "return/return-process@2.0", "related"],
        ["order/order-process@1.0", "return/return-process@2.0", undefined]]);
  });

  it("shows unresolved remote ownership separately from discovery status and keeps its failure", () => {
    const unresolved = mapBigPicture({ nodes: graph.nodes.slice(0, 1),
      edges: [{ ...graph.edges[0], target: null }], failures: [{ source, targetDomain: "return",
        targetProcess: "return-process", kind: "Unavailable", message: "Remote unavailable" }] });
    expect(unresolved.nodes[1].data).toMatchObject({ origin: "Remote", status: "Unresolved", version: "2.0" });
    expect(unresolved.edges[0].data?.failure).toContain("Unavailable: Remote unavailable");
  });

  it("renders process metadata with dark and light text classes", () => {
    const data = mapBigPicture(graph).nodes[0].data;
    const { container } = render(<ProcessNodeCard data={data} selected={false} id="node" type="process"
      dragging={false} isConnectable={false} zIndex={0} selectable={true} deletable={false} draggable={false} />);
    expect(screen.getByText("order-process")).toBeTruthy();
    expect(screen.getByText("Version 1.0")).toBeTruthy();
    expect(screen.getByText("Local")).toBeTruthy();
    expect(screen.getByText("Resolved")).toBeTruthy();
    expect(container.querySelector(".dark\\:text-zinc-50")).not.toBeNull();
  });

  it("adds available local YAML step names without inventing remote details", () => {
    const mapped = mapBigPicture(graph, [{ domain: "order", name: "order-process", version: "1.0", bigPicture: [],
      steps: [{ name: "payment", command: "Pay", timeoutLimit: null, maxAttempts: 1, compensation: null, transitions: [] }] }]);
    expect(mapped.nodes[0].data.steps).toEqual(["payment"]);
    expect(mapped.nodes[1].data.steps).toBeUndefined();
  });

  it("opens node and edge drawers and shows the discovery reason", async () => {
    const unresolved: DefinitionGraph = { nodes: graph.nodes.slice(0, 1),
      edges: [{ ...graph.edges[0], target: null }], failures: [{ source, targetDomain: "return",
        targetProcess: "return-process", kind: "Unavailable", message: "Remote unavailable" }] };
    const user = userEvent.setup();
    render(<ThemeProvider><BigPictureGraph graph={unresolved} /></ThemeProvider>);
    await user.click(screen.getByRole("button", { name: /View details for .*return-process/ }));
    const drawer = screen.getByRole("dialog", { name: "BigPicture details" });
    expect(within(drawer).getByText("Version")).toBeTruthy();
    expect(drawer.textContent).toContain("Remote unavailable");
    await user.click(screen.getByRole("button", { name: "Close BigPicture details" }));
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await user.click(screen.getByRole("button", { name: "related" }));
    expect(screen.getByRole("dialog").textContent).toContain("Source → target");
    expect(screen.getByRole("dialog").textContent).toContain("Remote unavailable");
  });
});
