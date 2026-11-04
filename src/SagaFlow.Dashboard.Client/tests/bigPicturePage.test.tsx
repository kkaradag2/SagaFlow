import { act, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DefinitionGraph, SagaDefinition } from "../src/api/sagaflowApi";
import { getDefinitionGraph } from "../src/api/sagaflowApi";
import { aggregateDefinitionGraphs, BigPicturePage } from "../src/pages/BigPicturePage";

vi.mock("../src/api/sagaflowApi", () => ({ getDefinitionGraph: vi.fn() }));
vi.mock("../src/components/BigPicture/BigPictureGraph", () => ({ default: ({ graph }: { graph: DefinitionGraph }) =>
  <div role="img" aria-label="Combined BigPicture graph"><span>{graph.edges[0]?.target ? "Resolved" : "Unresolved"}</span>{graph.edges.map((edge, index) =>
    <span key={index}>{edge.source.domain} → {edge.targetDomain}: {edge.relationship}</span>)}</div> }));

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

  it("clears topology when a refresh is denied", async () => {
    vi.useFakeTimers();
    vi.mocked(getDefinitionGraph).mockResolvedValueOnce(graph("order"))
      .mockRejectedValueOnce(new Error("SagaFlow request failed (403)."));
    render(<BigPicturePage definitions={[definition("order")]} refreshIntervalMs={1000} />);
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByRole("img", { name: "Combined BigPicture graph" })).toBeTruthy();
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(screen.getByRole("alert").textContent).toBe("Access to process relationships was denied.");
    expect(screen.queryByRole("img", { name: "Combined BigPicture graph" })).toBeNull();
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

  it("distinguishes graph API failure from an empty graph without leaking cached data", async () => {
    vi.mocked(getDefinitionGraph).mockRejectedValue(new Error("SagaFlow request failed (403)."));
    render(<BigPicturePage definitions={[definition("order")]} />);
    expect(await screen.findByRole("alert")).toHaveProperty("textContent", "Access to process relationships was denied.");
    expect(screen.queryByText("order → return: related")).toBeNull();
  });

  it("shows a true empty response as empty rather than an API error", async () => {
    vi.mocked(getDefinitionGraph).mockResolvedValue({ nodes: [], edges: [], failures: [] });
    render(<BigPicturePage definitions={[definition("order")]} />);
    expect(await screen.findByText("No process relationships available.")).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
