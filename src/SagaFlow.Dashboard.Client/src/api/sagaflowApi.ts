export type SagaFlowMetadata = {
  name: string;
  status: string;
};

export async function getMetadata(): Promise<SagaFlowMetadata> {
  const response = await fetch("/sagaflow/metadata");

  if (!response.ok) {
    throw new Error("Unable to load SagaFlow metadata.");
  }

  return response.json() as Promise<SagaFlowMetadata>;
}

export type SagaInstanceSummary = {
  id: string;
  processName: string;
  correlationId: string;
  status: string;
  currentStep: string | null;
  createdAt: string;
  updatedAt: string;
};

export type SagaStep = {
  stepName: string;
  command: string;
  status: string;
  executionOrder: number | null;
  startedAt: string | null;
  completedAt: string | null;
  timeoutLimit: string | null;
  timedOutAt: string | null;
  input: unknown;
  result: unknown;
};

export type SagaCompensation = {
  stepId: string;
  command: string;
  status: string;
  executionOrder: number | null;
  startedAt: string | null;
  completedAt: string | null;
  result: unknown;
};

export type SagaDomainRollback = {
  status: string;
  startedAt: string | null;
  completedAt: string | null;
  error: string | null;
};

export type SagaDomainStarter = {
  operationName: string;
  rollbackOperationName: string;
  status: string | null;
  startedAt: string | null;
  completedAt: string | null;
  input: unknown;
  result: unknown;
};

export type SagaInstanceDetail = SagaInstanceSummary & {
  definitionDomain: string;
  definitionVersion: string;
  failureReason: string | null;
  steps: SagaStep[];
  compensations: SagaCompensation[];
  domainRollback: SagaDomainRollback | null;
  domainStarter: SagaDomainStarter;
};

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, { headers: { Accept: "application/json" }, signal });
  if (!response.ok) throw new Error(`SagaFlow request failed (${response.status}).`);
  return response.json() as Promise<T>;
}

export function getInstances(): Promise<SagaInstanceSummary[]> {
  return getJson("api/instances");
}

export function getInstance(instanceId: string): Promise<SagaInstanceDetail> {
  return getJson(`api/instances/${encodeURIComponent(instanceId)}`);
}

// Read-only Dashboard projection of the existing parsed SagaDefinition model.
export type SagaConditionDefinition = {
  path: string;
  operator: string;
  value: string | number | boolean | null;
  valueJson?: string;
};

export type SagaTransitionDefinition = {
  event: string;
  when: string | null;
  condition: SagaConditionDefinition | null;
  else: boolean | null;
  nextStep: string;
};

export type SagaStepDefinition = {
  name: string;
  command: string;
  timeoutLimit: string | null;
  maxAttempts: number;
  compensation: { command: string } | null;
  transitions: SagaTransitionDefinition[];
};

export type SagaDefinition = {
  name: string;
  domain: string;
  version: string;
  steps: SagaStepDefinition[];
  bigPicture: { domain: string; process: string; version: string | null; endpoint: string }[];
};

export function getDefinitions(signal?: AbortSignal): Promise<SagaDefinition[]> {
  return getJson("api/definitions", signal);
}
