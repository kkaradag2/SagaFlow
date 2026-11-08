export type WorkflowIdentity = { domain: string; process: string; version: string | null };
export type InstanceContext = WorkflowIdentity & { version: string; instanceId: string };
export function instanceContextFromSearch(search: string): InstanceContext | null {
  const query = new URLSearchParams(search);
  const instanceId = query.get("instanceId"), domain = query.get("domain"), process = query.get("process"), version = query.get("version");
  return instanceId && domain && process && version ? { instanceId, domain, process, version } : null;
}
export function instanceContextQuery(context: InstanceContext | null): string {
  return context ? `?${new URLSearchParams(context)}` : "";
}

export function workflowPath(base: string, workflow: WorkflowIdentity): string {
  const path = `${base}/bigpicture/workflow/${encodeURIComponent(workflow.domain)}/${encodeURIComponent(workflow.process)}`;
  return workflow.version === null ? path : `${path}/${encodeURIComponent(workflow.version)}`;
}

export function workflowFromPath(path: string): WorkflowIdentity | null {
  const match = path.match(/\/bigpicture\/workflow\/([^/]+)\/([^/]+)(?:\/([^/]+))?\/?$/i);
  if (!match) return null;
  try {
    const domain = decodeURIComponent(match[1]), process = decodeURIComponent(match[2]);
    const version = match[3] === undefined ? null : decodeURIComponent(match[3]);
    return [domain, process, version].some(value => value?.includes("/")) ? null : { domain, process, version };
  } catch { return null; }
}
