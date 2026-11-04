export type WorkflowIdentity = { domain: string; process: string; version: string };

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
