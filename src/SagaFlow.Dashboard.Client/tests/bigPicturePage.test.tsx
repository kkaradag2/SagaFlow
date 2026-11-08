import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DefinitionGraph, SagaDefinition } from "../src/api/sagaflowApi";
import { getDefinitionGraph } from "../src/api/sagaflowApi";
import { aggregateDefinitionGraphs, BigPicturePage } from "../src/pages/BigPicturePage";

vi.mock("../src/api/sagaflowApi", () => ({ getDefinitionGraph: vi.fn() }));
vi.mock("../src/components/BigPicture/BigPictureGraph", () => ({ default: ({ graph }: { graph: DefinitionGraph }) =>
  <div role="img" aria-label="Combined BigPicture graph">
    {graph.nodes.map(node => <span key={`${node.domain}/${node.process}@${node.version}`}>{node.domain} · {node.origin}</span>)}
    <span>{graph.edges[0]?.target ? "Resolved" : "Unresolved"}</span>
    {graph.edges.map((edge, index) => <span key={index}>{edge.source.domain} → {edge.targetDomain}: {edge.relationship}</span>)}
  </div> }));

const definition = (domain: string): SagaDefinition => ({ name: `${domain}-process`, domain, version: "1.0", steps: [],
  bigPicture: [{ domain: "return", process: "return-process", version: "1.0", endpoint: "https://return.example", relationship: "related" }] });
const graph = (domain: string): DefinitionGraph => ({ nodes: [{ domain, process: `${domain}-process`, version: "1.0", origin: "Local" }],
  edges: [{ source: { domain, name: `${domain}-process`, version: "1.0" }, targetDomain: "return",
    targetProcess: "return-process", requestedVersion: "1.0", target: null, relationship: "related" }], failures: [] });

beforeEach(() => { vi.mocked(getDefinitionGraph).mockImplementation(async definition => graph(definition.domain)); });
afterEach(() => vi.useRealTimers());

describe("BigPicture page", () => {
  it("refreshes unresolved, resolved, unavailable and restarted states while open", async () => {
    vi.useFakeTimers();
    const unresolved = graph("order");
    const resolved: DefinitionGraph = { ...unresolved, nodes: [...unresolved.nodes,
      { domain: "return", process: "return-process", version: "1.0", origin: "Remote" }],
      edges: [{ ...unresolved.edges[0], target: { domain: "return", name: "return-process", version: "1.0" } }] };
    vi.mocked(getDefinitionGraph).mockResolvedValueOnce(unresolved).mockResolvedValueOnce(resolved)
      .mockResolvedValueOnce(unresolved).mockResolvedValueOnce(resolved);
    render(<BigPicturePage definitions={[definition("order")]} refreshIntervalMs={1000} />);
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText("Unresolved")).toBeTruthy();
    for (const expected of ["Resolved", "Unresolved", "Resolved"]) {
      await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
      expect(screen.getByText(expected)).toBeTruthy();
    }
    expect(getDefinitionGraph).toHaveBeenCalledTimes(4);
  });

  it("does not overlap calls and stops polling on unmount", async () => {
    vi.useFakeTimers();
    let finish!: (value: DefinitionGraph) => void;
    vi.mocked(getDefinitionGraph).mockResolvedValueOnce(graph("order"))
      .mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const view = render(<BigPicturePage definitions={[definition("order")]} refreshIntervalMs={1000} />);
    await act(async () => { await Promise.resolve(); });
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(getDefinitionGraph).toHaveBeenCalledTimes(2);
    view.unmount();
    finish(graph("order"));
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(getDefinitionGraph).toHaveBeenCalledTimes(2);
  });

  it("clears the denied root's previous graph while retaining Local nodes and another root's last successful graph", async () => {
    vi.useFakeTimers();
    vi.mocked(getDefinitionGraph).mockResolvedValueOnce(graph("order")).mockResolvedValueOnce(graph("returns"))
      .mockRejectedValueOnce(new Error("SagaFlow request failed (403)."))
      .mockRejectedValueOnce(new Error("SagaFlow request failed (503)."));
    render(<BigPicturePage definitions={[definition("order"), definition("returns")]} refreshIntervalMs={1000} />);
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText("order → return: related")).toBeTruthy();
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(screen.getByRole("alert").textContent).toContain("Access to process relationships was denied for order/order-process@1.0.");
    expect(screen.getByRole("alert").textContent).toContain("Unable to load or refresh process relationships for returns/returns-process@1.0.");
    expect(screen.getByText("order · Local")).toBeTruthy();
    expect(screen.getByText("returns · Local")).toBeTruthy();
    expect(screen.queryByText("order → return: related")).toBeNull();
    expect(screen.getByText("returns → return: related")).toBeTruthy();
  });

  it("ignores an old response after the local definitions change", async () => {
    let finish!: (value: DefinitionGraph) => void;
    vi.mocked(getDefinitionGraph).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }))
      .mockResolvedValueOnce(graph("returns"));
    const view = render(<BigPicturePage definitions={[definition("order")]} refreshIntervalMs={0} />);
    view.rerender(<BigPicturePage definitions={[definition("returns")]} refreshIntervalMs={0} />);
    await act(async () => { await Promise.resolve(); });
    finish(graph("order"));
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText("returns → return: related")).toBeTruthy();
    expect(screen.queryByText("order → return: related")).toBeNull();
  });
  it("keeps every local definition local regardless of discovery order", () => {
    const localB = { ...definition("return"), bigPicture: [] };
    const localA = definition("order");
    const aGraph: DefinitionGraph = { nodes: [
      { domain: "order", process: "order-process", version: "1.0", origin: "Local" },
      { domain: "return", process: "return-process", version: "1.0", origin: "Remote" },
      { domain: "return", process: "return-process", version: "2.0", origin: "Remote" }],
      edges: graph("order").edges, failures: [] };
    const bGraph: DefinitionGraph = { nodes: [{ domain: "return", process: "return-process", version: "1.0", origin: "Local" }], edges: [], failures: [] };
    for (const graphs of [[aGraph, bGraph], [bGraph, aGraph]]) {
      const merged = aggregateDefinitionGraphs(graphs, [localA, localB]);
      expect(merged.nodes.find(node => node.domain === "return" && node.version === "1.0")?.origin).toBe("Local");
      expect(merged.nodes.find(node => node.domain === "return" && node.version === "2.0")?.origin).toBe("Remote");
      expect(merged.edges).toHaveLength(1);
    }
    expect(aggregateDefinitionGraphs([aGraph], [localA, localB]).nodes.find(node =>
      node.domain === "return" && node.version === "1.0")?.origin).toBe("Local");
  });
  it("combines relationships from multiple local definitions", async () => {
    render(<BigPicturePage definitions={[definition("order"), definition("returns")] } />);
    expect(await screen.findByText("order → return: related")).toBeTruthy();
    expect(screen.getByText("returns → return: related")).toBeTruthy();
    expect(getDefinitionGraph).toHaveBeenCalledTimes(2);
  });

  it("keeps the successful Remote node and edge when a different root fails", async () => {
    const remote = { domain: "return", name: "return-process", version: "1.0" };
    const successful = graph("returns");
    vi.mocked(getDefinitionGraph).mockRejectedValueOnce(new Error("SagaFlow request failed (503)."))
      .mockResolvedValueOnce({ nodes: [...successful.nodes, { domain: remote.domain, process: remote.name,
        version: remote.version, origin: "Remote", definition: definition(remote.domain) }],
        edges: [{ ...successful.edges[0], target: remote }], failures: [] });
    render(<BigPicturePage definitions={[definition("order"), definition("returns")]} />);
    expect(await screen.findByText("returns → return: related")).toBeTruthy();
    expect(screen.getByText("return · Remote")).toBeTruthy();
    expect(screen.getByText("order · Local")).toBeTruthy();
    expect(screen.queryByText("order → return: related")).toBeNull();
    expect(screen.getByRole("alert").textContent).toContain("Unable to load or refresh process relationships for order/order-process@1.0.");
  });

  it("shows only Local definitions and an error after initial graph access is denied", async () => {
    vi.mocked(getDefinitionGraph).mockRejectedValue(new Error("SagaFlow request failed (403)."));
    render(<BigPicturePage definitions={[definition("order")]} />);
    expect(await screen.findByText("order · Local")).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toContain("Access to process relationships was denied");
    expect(screen.queryByText("return · Remote")).toBeNull();
    expect(screen.queryByText("order → return: related")).toBeNull();
  });

  it("keeps the Local node when discovery returns an empty graph", async () => {
    vi.mocked(getDefinitionGraph).mockResolvedValue({ nodes: [], edges: [], failures: [] });
    render(<BigPicturePage definitions={[definition("order")]} />);
    expect(await screen.findByRole("img", { name: "Combined BigPicture graph" })).toBeTruthy();
    expect(aggregateDefinitionGraphs([], [definition("order")]).nodes).toEqual([
      { domain: "order", process: "order-process", version: "1.0", origin: "Local" },
    ]);
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
