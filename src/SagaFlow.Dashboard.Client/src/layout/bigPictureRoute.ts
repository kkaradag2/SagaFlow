export type WorkflowIdentity = { domain: string; process: string; version: string };
export type InstanceContext = WorkflowIdentity & { instanceId: string };
export function instanceContextFromSearch(search: string): InstanceContext | null {
  const query = new URLSearchParams(search);
  const instanceId = query.get("instanceId"), domain = query.get("domain"), process = query.get("process"), version = query.get("version");
  return instanceId && domain && process && version ? { instanceId, domain, process, version } : null;
}
export function instanceContextQuery(context: InstanceContext | null): string {
  return context ? `?${new URLSearchParams(context)}` : "";
}

export function workflowPath(base: string, workflow: WorkflowIdentity): string {
  return `${base}/bigpicture/workflow/${encodeURIComponent(workflow.domain)}/${encodeURIComponent(workflow.process)}/${encodeURIComponent(workflow.version)}`;
}

export function workflowFromPath(path: string): WorkflowIdentity | null {
  const match = path.match(/\/bigpicture\/workflow\/([^/]+)\/([^/]+)\/([^/]+)\/?$/i);
  if (!match) return null;
  try {
    const [domain, process, version] = match.slice(1).map(decodeURIComponent);
    return domain && process && version && ![domain, process, version].some(value => value.includes("/"))
      ? { domain, process, version } : null;
  } catch { return null; }
}
