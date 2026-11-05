import { useCallback, useEffect, useRef, useState } from "react";
import { getInstanceRelationships, type InstanceContext, type InstanceRelationship } from "../api/sagaflowApi";
import { useSagaFlowLiveUpdates } from "./useSagaFlowLiveUpdates";

type State = { key: string; items: InstanceRelationship[]; nextOffset: number | null; loading: boolean; error: string | null };
const empty: State = { key: "", items: [], nextOffset: null, loading: false, error: null };
export function useInstanceRelationships(context: InstanceContext | null) {
  const key = context ? JSON.stringify([context.instanceId, context.domain, context.process, context.version]) : "";
  const [state, setState] = useState<State>(empty);
  const actions = useRef({ refresh: () => {}, more: () => {} });
  useEffect(() => {
    if (!key) { setState(empty); actions.current = { refresh: () => {}, more: () => {} }; return; }
    const [instanceId, domain, process, version] = JSON.parse(key) as string[];
    const current = { instanceId, domain, process, version };
    let disposed = false, busy = false, queued = false;
    let abort: AbortController | undefined;
    let items: InstanceRelationship[] = [], nextOffset: number | null = null;
    const run = async (append: boolean) => {
      if (disposed) return;
      if (busy) { if (!append) queued = true; return; }
      busy = true;
      const offset = append ? nextOffset : 0;
      if (offset === null) { busy = false; return; }
      abort = new AbortController();
      setState(previous => ({ ...(previous.key === key ? previous : empty), key, loading: true, error: null }));
      try {
        const page = await getInstanceRelationships(current, offset, abort.signal);
        if (disposed) return;
        if (page.localInstanceId !== instanceId || (page.nextOffset !== null && page.nextOffset <= offset))
          throw new Error("Invalid instance relationship page.");
        const unique = new Map((append ? items : []).map(row => [`${row.perspective}:${row.invocationId}`, row]));
        for (const row of page.items) unique.set(`${row.perspective}:${row.invocationId}`, row);
        items = [...unique.values()]; nextOffset = page.nextOffset;
        setState({ key, items, nextOffset, loading: false, error: null });
      } catch (error) {
        if (!disposed) {
          // Do not retain runtime highlights after a failed refresh or revoked access.
          items = []; nextOffset = null;
          setState({ key, items, nextOffset, loading: false, error: error instanceof Error ? error.message : "Unable to load runtime relationships." });
        }
      } finally {
        busy = false;
        if (!disposed && queued) { queued = false; void run(false); }
      }
    };
    actions.current = { refresh: () => { void run(false); }, more: () => { void run(true); } };
    void run(false);
    return () => { disposed = true; abort?.abort(); actions.current = { refresh: () => {}, more: () => {} }; };
  }, [key]);
  const onChange = useCallback((id: string | null) => { if (context && (id === null || id === context.instanceId)) actions.current.refresh(); }, [context?.instanceId]);
  useSagaFlowLiveUpdates(onChange, Boolean(key));
  return { ...(state.key === key ? state : { ...empty, key, loading: Boolean(key) }),
    refresh: () => actions.current.refresh(), loadMore: () => actions.current.more() };
}
