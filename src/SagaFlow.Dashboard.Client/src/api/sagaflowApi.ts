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

export type ProcessIdentity = { domain: string; process: string; version: string };
export type InvocationView = {
  invocationId: string; target: ProcessIdentity; targetSagaInstanceId: string | null;
  lastKnownStatus: string | null; createdAt: string | null; updatedAt: string | null; lastObservedAt: string | null;
  reconciliation: { reason: string; requiredAt: string } | null;
};
export type IncomingInvocationView = {
  invocationId: string; source: ProcessIdentity; sourceSagaInstanceId: string; sourceStep: string;
  sourceCorrelationId: string; sourceRequestCreatedAt: string; acceptedAt: string;
};
export type SagaInstanceSummary = {
  id: string;
  processName: string;
  definitionDomain?: string;
  definitionVersion?: string;
  origin?: "Local" | "External";
  correlationId: string;
  status: string;
  currentStep: string | null;
  createdAt: string;
  updatedAt: string;
};

export type SagaStep = {
  stepName: string;
  external?: boolean;
  invocation?: InvocationView | null;
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
  incomingInvocation?: IncomingInvocationView | null;
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
  bigPicture: { domain: string; process: string; version: string | null; relationship?: "related" | null }[];
};

export type DefinitionIdentity = { domain: string; name: string; version: string };
export type DefinitionGraph = {
  nodes: { domain: string; process: string; version: string; origin: "Local" | "Remote"; definition?: SagaDefinition }[];
  edges: { source: DefinitionIdentity; targetDomain: string; targetProcess: string;
    requestedVersion: string | null; target: DefinitionIdentity | null; relationship: "related" | null }[];
  failures: { source: DefinitionIdentity; targetDomain: string; targetProcess: string;
    requestedVersion?: string | null; kind: string; message: string }[];
};

export function getDefinitionGraph(definition: SagaDefinition, signal?: AbortSignal): Promise<DefinitionGraph> {
  const query = new URLSearchParams({ domain: definition.domain, process: definition.name, version: definition.version });
  return getJson(`api/definition-graph?${query}`, signal);
}

export function getDefinitions(signal?: AbortSignal): Promise<SagaDefinition[]> {
  return getJson("api/definitions", signal);
}
