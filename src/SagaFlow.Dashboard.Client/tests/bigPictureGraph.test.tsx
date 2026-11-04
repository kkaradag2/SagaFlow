import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { DefinitionGraph, SagaDefinition } from "../src/api/sagaflowApi";
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
    ReactFlow: ({ nodes, edges, onNodeClick, onEdgeClick }: {
      nodes: ReturnType<typeof mapBigPicture>["nodes"]; edges: ReturnType<typeof mapBigPicture>["edges"];
      onNodeClick: (event: unknown, node: unknown) => void; onEdgeClick: (event: unknown, edge: unknown) => void;
    }) => <div data-testid="bigpicture-flow">
      {nodes.map(node => <button type="button" key={node.id} onClick={event => onNodeClick(event, node)}>
        <ProcessNodeCard data={node.data} selected={false} id={node.id} type="process" dragging={false}
          isConnectable={false} zIndex={0} selectable={true} deletable={false} draggable={false}
          selected={false} /></button>)}
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

describe("BigPicture graph", () => {
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
    await user.click(screen.getByRole("button", { name: /order-process/ }));
    expect(await screen.findByRole("img", { name: "order-process workflow diagram" })).toHaveProperty("textContent", "order-step");
    await user.click(screen.getByRole("button", { name: "Back to BigPicture" }));
    await user.click(screen.getByRole("button", { name: /return-process/ }));
    expect(await screen.findByRole("img", { name: "return-process workflow diagram" })).toHaveProperty("textContent", "return-step");
  });

  it("encodes and parses an exact workflow identity for direct routes", () => {
    const identity = { domain: "return", process: "return-process", version: "2.0 beta" };
    const path = workflowPath("/sagaflow", identity);
    expect(path).toBe("/sagaflow/bigpicture/workflow/return/return-process/2.0%20beta");
    expect(workflowFromPath(path)).toEqual(identity);
    expect(workflowFromPath("/sagaflow/bigpicture/workflow/return/return-process")).toBeNull();
    expect(workflowFromPath("/sagaflow/bigpicture/workflow/return/return-process/%ZZ")).toBeNull();
  });

  it.each([
    { domain: "missing", process: "return-process", version: "2.0" },
    { domain: "return", process: "missing", version: "2.0" },
    { domain: "return", process: "return-process", version: "9.9" },
  ])("does not invent a graph for unknown workflow identity $domain/$process@$version", async workflow => {
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
    await user.click(screen.getByRole("button", { name: /return-process/ }));
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
    await user.click(screen.getByRole("button", { name: /return-process/ }));
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
